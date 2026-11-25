using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;

namespace StudyGuard;

/// <summary>
/// Per-user Windows proxy that blocks website domains listed by the current activity.
/// Previous proxy settings are saved and restored when the feature is disabled.
/// </summary>
internal sealed class SystemWebsiteProxy : IDisposable
{
    private const int ProxyPort = 17842;
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly string backupFilePath;
    private readonly object hostsLock = new();
    private HashSet<string> blockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> alwaysBlockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> allowedExceptionHosts = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> activityAllowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private bool blockAllExceptActivityAllowed;
    private TcpListener? listener;
    private CancellationTokenSource? listenerCancellation;

    public SystemWebsiteProxy(string backupFilePath)
    {
        this.backupFilePath = backupFilePath;
    }

    public bool IsRunning => listener is not null;
    public event Action<string>? BlockedHostRequested;

    public bool TryRecoverPreviousSettings(out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(backupFilePath))
                RestoreRegistrySettings();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void ApplyBlockList(IEnumerable<string> blocked, IEnumerable<string> allowedExceptions,
        bool blockAllExcept = false, IEnumerable<string>? activityAllowed = null,
        IEnumerable<string>? alwaysBlocked = null)
    {
        lock (hostsLock)
        {
            blockedHosts = blocked.ToHashSet(StringComparer.OrdinalIgnoreCase);
            alwaysBlockedHosts = (alwaysBlocked ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            allowedExceptionHosts = allowedExceptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            blockAllExceptActivityAllowed = blockAllExcept;
            activityAllowedHosts = (activityAllowed ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        bool startListener = listener is null;
        if (startListener)
        {
            SaveRegistrySnapshot();
            listenerCancellation = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Loopback, ProxyPort);
            listener.Start();
            _ = AcceptConnectionsAsync(listener, listenerCancellation.Token);
        }

        try
        {
            EnsureWindowsProxySettings();
        }
        catch
        {
            if (startListener)
            {
                StopListener();
                RestoreRegistrySettings(forceRestore: true);
            }
            throw;
        }
    }

    private void EnsureWindowsProxySettings()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException("تعذر فتح إعدادات الإنترنت الخاصة بالمستخدم.");
        string proxyServer = $"http=127.0.0.1:{ProxyPort};https=127.0.0.1:{ProxyPort}";
        bool alreadyApplied = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1 &&
            string.Equals(key.GetValue("ProxyServer") as string, proxyServer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(key.GetValue("ProxyOverride") as string, "localhost;127.*", StringComparison.OrdinalIgnoreCase) &&
            key.GetValue("AutoConfigURL") is null;
        if (alreadyApplied)
            return;

        // If another program replaced or disabled the proxy, preserve that current setting
        // when no restore snapshot exists before reasserting StudyGuard's active policy.
        SaveRegistrySnapshot();
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", "localhost;127.*", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        NotifyInternetSettingsChanged();
    }

    public void StopAndRestore()
    {
        if (File.Exists(backupFilePath))
            RestoreRegistrySettings();
        StopListener();
    }

    private void SaveRegistrySnapshot()
    {
        if (File.Exists(backupFilePath))
            return;

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        var snapshot = new ProxySettingsSnapshot
        {
            ProxyEnable = key?.GetValue("ProxyEnable") is object enabled ? Convert.ToInt32(enabled) : null,
            ProxyServer = key?.GetValue("ProxyServer") as string,
            ProxyOverride = key?.GetValue("ProxyOverride") as string,
            AutoConfigUrl = key?.GetValue("AutoConfigURL") as string
        };

        string? directory = Path.GetDirectoryName(backupFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = backupFilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, backupFilePath, overwrite: false);
    }

    private void RestoreRegistrySettings(bool forceRestore = false)
    {
        ProxySettingsSnapshot snapshot = JsonSerializer.Deserialize<ProxySettingsSnapshot>(File.ReadAllText(backupFilePath))
            ?? throw new InvalidDataException("ملف استعادة إعدادات الإنترنت غير صالح.");

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException("تعذر فتح إعدادات الإنترنت لاستعادتها.");

        bool stillManagedByStudyGuard =
            Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1 &&
            string.Equals(
                key.GetValue("ProxyServer") as string,
                $"http=127.0.0.1:{ProxyPort};https=127.0.0.1:{ProxyPort}",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                key.GetValue("ProxyOverride") as string,
                "localhost;127.*",
                StringComparison.OrdinalIgnoreCase) &&
            key.GetValue("AutoConfigURL") is null;
        if (!forceRestore && !stillManagedByStudyGuard)
        {
            // Preserve a proxy setting the user or another application changed while StudyGuard was active.
            File.Delete(backupFilePath);
            return;
        }

        RestoreValue(key, "ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
        RestoreValue(key, "ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
        RestoreValue(key, "ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
        RestoreValue(key, "AutoConfigURL", snapshot.AutoConfigUrl, RegistryValueKind.String);
        NotifyInternetSettingsChanged();
        File.Delete(backupFilePath);
    }

    private static void RestoreValue(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value, kind);
    }

    private void StopListener()
    {
        listenerCancellation?.Cancel();
        listener?.Stop();
        listener = null;
        listenerCancellation?.Dispose();
        listenerCancellation = null;
    }

    private async Task AcceptConnectionsAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await activeListener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientSafelyAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch
            {
                // A disconnected browser or remote server should not stop the proxy.
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream clientStream = client.GetStream();
        byte[] headerBytes = await ReadHeaderAsync(clientStream, cancellationToken);
        string headerText = Encoding.Latin1.GetString(headerBytes);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
            return;

        string[] requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 3)
        {
            await SendDeniedAsync(clientStream, cancellationToken);
            return;
        }

        string method = requestParts[0];
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConnectAsync(client, clientStream, requestParts[1], cancellationToken);
            return;
        }

        await HandleHttpRequestAsync(client, clientStream, requestParts, lines, cancellationToken);
    }

    private async Task HandleConnectAsync(TcpClient client, NetworkStream clientStream, string authority, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out Uri? targetUri))
        {
            await SendDeniedAsync(clientStream, cancellationToken);
            return;
        }
        if (!IsHostAllowed(targetUri.Host, client))
        {
            BlockedHostRequested?.Invoke(targetUri.Host);
            await SendDeniedAsync(clientStream, cancellationToken);
            return;
        }

        using var remoteClient = new TcpClient();
        await remoteClient.ConnectAsync(targetUri.Host, targetUri.Port == 80 ? 443 : targetUri.Port, cancellationToken);
        NetworkStream remoteStream = remoteClient.GetStream();
        byte[] established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await clientStream.WriteAsync(established, cancellationToken);
        await RelayAsync(clientStream, remoteStream, cancellationToken);
    }

    private async Task HandleHttpRequestAsync(
        TcpClient client,
        NetworkStream clientStream,
        string[] requestParts,
        string[] headerLines,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(requestParts[1], UriKind.Absolute, out Uri? targetUri))
        {
            string hostHeader = headerLines.FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";
            if (!Uri.TryCreate("http://" + hostHeader + requestParts[1], UriKind.Absolute, out targetUri))
            {
                await SendDeniedAsync(clientStream, cancellationToken);
                return;
            }
        }

        if (!IsHostAllowed(targetUri.Host, client))
        {
            BlockedHostRequested?.Invoke(targetUri.Host);
            await SendDeniedAsync(clientStream, cancellationToken);
            return;
        }

        using var remoteClient = new TcpClient();
        await remoteClient.ConnectAsync(targetUri.Host, targetUri.Port, cancellationToken);
        NetworkStream remoteStream = remoteClient.GetStream();

        var forwardedHeaders = new List<string>
        {
            $"{requestParts[0]} {targetUri.PathAndQuery} {requestParts[2]}"
        };
        bool hasConnectionHeader = false;
        foreach (string line in headerLines.Skip(1).Where(line => line.Length > 0))
        {
            if (line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                forwardedHeaders.Add("Connection: close");
                hasConnectionHeader = true;
                continue;
            }
            if (line.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase))
                continue;
            forwardedHeaders.Add(line);
        }
        if (!hasConnectionHeader)
            forwardedHeaders.Add("Connection: close");
        forwardedHeaders.Add("");
        forwardedHeaders.Add("");
        await remoteStream.WriteAsync(Encoding.Latin1.GetBytes(string.Join("\r\n", forwardedHeaders)), cancellationToken);

        if (headerLines.Any(line => line.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)))
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), cancellationToken);

        string? contentLengthLine = headerLines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLengthLine is not null && long.TryParse(contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..].Trim(), out long contentLength))
            await CopyBytesAsync(clientStream, remoteStream, contentLength, cancellationToken);
        else if (headerLines.Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && line.Contains("chunked", StringComparison.OrdinalIgnoreCase)))
            await CopyChunkedBodyAsync(clientStream, remoteStream, cancellationToken);

        await remoteStream.CopyToAsync(clientStream, cancellationToken);
    }

    private bool IsHostAllowed(string host, TcpClient client)
    {
        if (IsYtDlpException(host, GetClientProcessPath(client)))
            return true;
        lock (hostsLock)
        {
            return IsHostAllowedByPolicy(host, blockedHosts, alwaysBlockedHosts, allowedExceptionHosts,
                blockAllExceptActivityAllowed, activityAllowedHosts);
        }
    }

    internal static bool IsYtDlpException(string host, string? executablePath) =>
        IsApprovedYtDlpPath(executablePath) && IsYtDlpHost(host);

    internal static bool IsApprovedYtDlpPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        string[] approvedPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "yt-dlp.exe"),
            @"E:\youtube downloader\yt-dlp.exe"
        };
        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch { return false; }
        return approvedPaths.Any(path => File.Exists(path) && string.Equals(Path.GetFullPath(path), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsYtDlpHost(string rawHost)
    {
        string host = rawHost.TrimEnd('.').ToLowerInvariant();
        return host is "youtube.com" or "youtu.be" or "youtubei.googleapis.com" ||
            host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
            host.EndsWith(".googlevideo.com", StringComparison.Ordinal);
    }

    private static string? GetClientProcessPath(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || client.Client.LocalEndPoint is not IPEndPoint local)
            return null;
        int? processId = FindTcpOwnerProcessId((ushort)remote.Port, (ushort)local.Port);
        if (processId is null)
            return null;
        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            return process.MainModule?.FileName;
        }
        catch { return null; }
    }

    internal static int? FindTcpOwnerProcessId(ushort clientLocalPort, ushort proxyPort)
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0);
        if (size <= 0 || size > 16 * 1024 * 1024)
            return null;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, true, 2, 5, 0) != 0)
                return null;
            int count = Marshal.ReadInt32(table);
            IntPtr row = IntPtr.Add(table, sizeof(uint));
            int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (int index = 0; index < count; index++, row = IntPtr.Add(row, rowSize))
            {
                TcpRowOwnerPid entry = Marshal.PtrToStructure<TcpRowOwnerPid>(row);
                ushort boundPort = unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(entry.LocalPort & 0xffff))));
                ushort clientPort = unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(entry.RemotePort & 0xffff))));
                if (boundPort == clientLocalPort && clientPort == proxyPort && entry.State == 5)
                    return checked((int)entry.OwningProcessId);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    internal static bool IsHostAllowedByPolicy(string host, IEnumerable<string> blocked, IEnumerable<string> alwaysBlocked,
        IEnumerable<string> allowedExceptions, bool blockAllExcept, IEnumerable<string> activityAllowed)
    {
        string normalizedHost = host.TrimEnd('.').ToLowerInvariant();
        static bool Matches(string normalized, string candidate) =>
            normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase);

        if (alwaysBlocked.Any(candidate => Matches(normalizedHost, candidate)))
            return false;
        if (allowedExceptions.Any(candidate => Matches(normalizedHost, candidate)))
            return true;
        if (blockAllExcept)
            return activityAllowed.Any(candidate => Matches(normalizedHost, candidate));
        return !blocked.Any(candidate => Matches(normalizedHost, candidate));
    }

    private static async Task<byte[]> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] oneByte = new byte[1];
        while (buffer.Length < 32 * 1024)
        {
            int count = await stream.ReadAsync(oneByte, cancellationToken);
            if (count == 0)
                break;
            buffer.WriteByte(oneByte[0]);
            if (buffer.Length >= 4)
            {
                byte[] bytes = buffer.GetBuffer();
                int length = (int)buffer.Length;
                if (bytes[length - 4] == 13 && bytes[length - 3] == 10 && bytes[length - 2] == 13 && bytes[length - 1] == 10)
                    return buffer.ToArray();
            }
        }
        throw new InvalidDataException("HTTP headers were incomplete or too large.");
    }

    private static async Task CopyBytesAsync(Stream source, Stream destination, long bytesToCopy, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        while (bytesToCopy > 0)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytesToCopy)), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesToCopy -= read;
        }
    }

    private static async Task CopyChunkedBodyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            string sizeLine = await ReadLineAsync(source, cancellationToken);
            await WriteLineAsync(destination, sizeLine, cancellationToken);
            string sizeText = sizeLine.Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out int chunkSize))
                throw new InvalidDataException("Invalid HTTP chunk size.");
            if (chunkSize == 0)
            {
                string trailer;
                do
                {
                    trailer = await ReadLineAsync(source, cancellationToken);
                    await WriteLineAsync(destination, trailer, cancellationToken);
                } while (trailer.Length > 0);
                return;
            }

            await CopyBytesAsync(source, destination, chunkSize + 2L, cancellationToken);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] oneByte = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            int count = await stream.ReadAsync(oneByte, cancellationToken);
            if (count == 0)
                throw new EndOfStreamException();
            buffer.WriteByte(oneByte[0]);
            if (oneByte[0] == 10)
                return Encoding.Latin1.GetString(buffer.ToArray()).TrimEnd('\r', '\n');
        }
        throw new InvalidDataException("HTTP line was too long.");
    }

    private static Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n"), cancellationToken).AsTask();
    }

    private static async Task RelayAsync(Stream client, Stream remote, CancellationToken cancellationToken)
    {
        Task clientToRemote = client.CopyToAsync(remote, cancellationToken);
        Task remoteToClient = remote.CopyToAsync(client, cancellationToken);
        await Task.WhenAny(clientToRemote, remoteToClient);
    }

    private static Task SendDeniedAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.ASCII.GetBytes("StudyGuard blocked this website by schedule.");
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n");
        byte[] response = header.Concat(body).ToArray();
        return stream.WriteAsync(response, cancellationToken).AsTask();
    }

    private static void NotifyInternetSettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        IntPtr settingName = Marshal.StringToHGlobalUni(InternetSettingsKey);
        try
        {
            _ = SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, settingName,
                0x0002, 2000, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(settingName);
        }
    }

    public void Dispose() => StopAndRestore();

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcessId;
    }

    private sealed class ProxySettingsSnapshot
    {
        public ProxySettingsSnapshot() { }

        public int? ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
        public string? AutoConfigUrl { get; set; }
    }
}
