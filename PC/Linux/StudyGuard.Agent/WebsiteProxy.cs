using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StudyGuard.Agent;

internal static class WebsitePolicy
{
    internal static HashSet<string> GetBlockedSites(StudyData config, ActivityRule? active)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.PermanentSites)
        {
            var domain = ConfigReader.NormalizeDomain(entry.Value);
            if (domain.Length > 0) blocked.Add(domain);
        }
        var exceptions = config.ExceptionSites.Select(ConfigReader.NormalizeDomain).Where(x => x.Length > 0).ToList();
        if (active?.BlockSites == true)
            foreach (var raw in active.Sites)
            {
                var domain = ConfigReader.NormalizeDomain(raw);
                if (domain.Length > 0 && !exceptions.Any(exception => ConfigReader.Matches(domain, exception)) && !blocked.Contains(domain))
                    blocked.Add(domain);
            }
        return blocked;
    }

    internal static IEnumerable<string> GetHostsDomains(IEnumerable<string> domains, bool proxyActive) =>
        proxyActive ? domains.Where(domain => !IsYouTubeHost(domain)) : domains;

    internal static bool IsYouTubeHost(string rawHost)
    {
        var host = rawHost.TrimEnd('.').ToLowerInvariant();
        return host is "youtube.com" or "youtu.be" or "youtubei.googleapis.com" or "googlevideo.com" ||
            host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
            host.EndsWith(".googlevideo.com", StringComparison.Ordinal);
    }

    internal static bool IsApprovedYtDlpPath(string? path, string homeDirectory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return false;
        var allowed = new[]
        {
            "/usr/bin/yt-dlp",
            "/usr/local/bin/yt-dlp",
            Path.Combine(homeDirectory, ".local/bin/yt-dlp")
        };
        try
        {
            var actual = Path.GetFullPath(path);
            if (!allowed.Any(candidate => string.Equals(Path.GetFullPath(candidate), actual, StringComparison.Ordinal)) ||
                !File.Exists(actual))
                return false;
            var mode = File.GetUnixFileMode(actual);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsAllowed(string host, string? executablePath, string homeDirectory,
        IEnumerable<string> blockedDomains, IEnumerable<string> permanentlyBlockedDomains)
    {
        bool permanentlyBlocked = permanentlyBlockedDomains.Any(domain => ConfigReader.Matches(host, domain)) ||
            permanentlyBlockedDomains.Any(domain => ConfigReader.Matches("youtube.com", domain) && IsYouTubeHost(host));
        if (permanentlyBlocked)
            return IsApprovedYtDlpPath(executablePath, homeDirectory) && IsYouTubeHost(host);

        bool blocked = blockedDomains.Any(domain => ConfigReader.Matches(host, domain)) ||
            blockedDomains.Any(domain => ConfigReader.Matches("youtube.com", domain) && IsYouTubeHost(host));
        if (!blocked)
            return true;

        return IsApprovedYtDlpPath(executablePath, homeDirectory) && IsYouTubeHost(host);
    }
}

internal static class LinuxProcessInspector
{
    internal static string? GetExecutablePath(TcpClient client, int expectedUid, string homeDirectory)
    {
        if (client.Client.LocalEndPoint is not IPEndPoint local ||
            client.Client.RemoteEndPoint is not IPEndPoint remote)
            return null;

        var inode = FindClientSocketInode(local, remote, expectedUid);
        if (inode is null) return null;

        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), out var pid)) continue;
            var startTime = ReadStartTime(processDirectory);
            if (startTime is null || ReadUid(Path.Combine(processDirectory, "status")) != expectedUid)
                continue;

            var fileDescriptors = Path.Combine(processDirectory, "fd");
            try
            {
                if (!Directory.EnumerateFileSystemEntries(fileDescriptors)
                    .Any(fd => string.Equals(new FileInfo(fd).LinkTarget, $"socket:[{inode}]", StringComparison.Ordinal)))
                    continue;

                var executablePath = new FileInfo(Path.Combine(processDirectory, "exe")).ResolveLinkTarget(true)?.FullName;
                var commandLine = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(processDirectory, "cmdline")))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var approvedEntryPoint = commandLine.Take(2).FirstOrDefault(argument =>
                    WebsitePolicy.IsApprovedYtDlpPath(argument, homeDirectory));
                return ReadStartTime(processDirectory) == startTime ? approvedEntryPoint ?? executablePath : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    private static long? FindClientSocketInode(IPEndPoint proxy, IPEndPoint peer, int expectedUid)
    {
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table)) continue;
            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 10 || fields[3] != "01" ||
                    !int.TryParse(fields[7], out var uid) || uid != expectedUid ||
                    !long.TryParse(fields[9], out var inode))
                    continue;

                if (TryParseEndpoint(fields[1], out var local) &&
                    TryParseEndpoint(fields[2], out var remote) &&
                    local.Port == peer.Port && remote.Port == proxy.Port &&
                    SameAddress(local.Address, peer.Address) && SameAddress(remote.Address, proxy.Address))
                    return inode;
            }
        }
        return null;
    }

    private static bool SameAddress(IPAddress first, IPAddress second)
    {
        if (first.IsIPv4MappedToIPv6) first = first.MapToIPv4();
        if (second.IsIPv4MappedToIPv6) second = second.MapToIPv4();
        return first.Equals(second);
    }

    private static bool TryParseEndpoint(string value, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Any, 0);
        var separator = value.LastIndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(value[(separator + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var port))
            return false;

        var addressText = value[..separator];
        if (addressText.Length is not (8 or 32)) return false;
        var bytes = Convert.FromHexString(addressText);
        for (var index = 0; index < bytes.Length; index += 4)
            Array.Reverse(bytes, index, 4);
        endpoint = new IPEndPoint(new IPAddress(bytes), port);
        return true;
    }

    private static int ReadUid(string statusPath)
    {
        foreach (var line in File.ReadLines(statusPath))
            if (line.StartsWith("Uid:", StringComparison.Ordinal) &&
                int.TryParse(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var uid))
                return uid;
        return -1;
    }

    private static long? ReadStartTime(string processDirectory)
    {
        try
        {
            var stat = File.ReadAllText(Path.Combine(processDirectory, "stat"));
            var close = stat.LastIndexOf(')');
            var fields = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return close >= 0 && fields.Length > 19 && long.TryParse(fields[19], out var startTime) ? startTime : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class DesktopWebsiteProxy : IDisposable
{
    internal const int Port = 17842;
    private const string ProxySchema = "org.gnome.system.proxy";
    private readonly int uid;
    private readonly string userName;
    private readonly string homeDirectory;
    private readonly string snapshotPath;
    private readonly object lifecycleLock = new();
    private readonly object policyLock = new();
    private HashSet<string> blockedDomains = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> permanentDomains = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private bool proxySettingsApplied;
    private DateTimeOffset nextSettingsAttemptUtc;
    private readonly string[] managedSettings =
    [
        $"{ProxySchema} mode",
        $"{ProxySchema} autoconfig-url",
        $"{ProxySchema} ignore-hosts",
        $"{ProxySchema} use-same-proxy",
        "org.gnome.system.proxy.http host",
        "org.gnome.system.proxy.http port",
        "org.gnome.system.proxy.https host",
        "org.gnome.system.proxy.https port"
    ];

    internal DesktopWebsiteProxy(int uid, string userName, string homeDirectory, string stateDirectory)
    {
        this.uid = uid;
        this.userName = userName;
        this.homeDirectory = homeDirectory;
        snapshotPath = Path.Combine(stateDirectory, $"gnome-proxy-{uid}.json");
    }

    internal bool Update(HashSet<string> blocked, HashSet<string> permanent)
    {
        lock (lifecycleLock)
            return UpdateCore(blocked, permanent);
    }

    private bool UpdateCore(HashSet<string> blocked, HashSet<string> permanent)
    {
        var shouldRun = blocked.Any(WebsitePolicy.IsYouTubeHost);
        if (!shouldRun)
        {
            if (File.Exists(snapshotPath) && listener is null)
            {
                lock (policyLock)
                {
                    blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    permanentDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                StartListener();
            }
            StopAndRestore();
            return false;
        }

        lock (policyLock)
        {
            blockedDomains = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
            permanentDomains = new HashSet<string>(permanent, StringComparer.OrdinalIgnoreCase);
        }

        if (listener is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            SaveProxySettings();
            StartListener();
        }

        var now = DateTimeOffset.UtcNow;
        if (now < nextSettingsAttemptUtc) return proxySettingsApplied;
        try
        {
            ApplyProxySettings();
            proxySettingsApplied = true;
            nextSettingsAttemptUtc = now.AddSeconds(30);
            return true;
        }
        catch
        {
            proxySettingsApplied = false;
            nextSettingsAttemptUtc = now.AddSeconds(10);
            throw;
        }
    }

    internal void RestoreOnly() => StopAndRestore();

    private void StartListener()
    {
        var source = new CancellationTokenSource();
        var activeListener = new TcpListener(IPAddress.Loopback, Port);
        try { activeListener.Start(); }
        catch
        {
            source.Dispose();
            throw;
        }
        cancellation = source;
        listener = activeListener;
        _ = AcceptConnectionsAsync(activeListener, source.Token);
    }

    internal void StopAndRestore()
    {
        lock (lifecycleLock)
        {
            if (File.Exists(snapshotPath))
                RestoreProxySettings();
            cancellation?.Cancel();
            listener?.Stop();
            listener = null;
            cancellation?.Dispose();
            cancellation = null;
            proxySettingsApplied = false;
            nextSettingsAttemptUtc = DateTimeOffset.MinValue;
        }
    }

    private async Task AcceptConnectionsAsync(TcpListener activeListener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await activeListener.AcceptTcpClientAsync(token);
                _ = HandleClientSafelyAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException exception) when (token.IsCancellationRequested)
        {
            Console.Error.WriteLine($"StudyGuard proxy listener stopped: {exception.Message}");
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try { await HandleClientAsync(client, token); }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException)
            {
                Console.Error.WriteLine($"StudyGuard proxy request failed: {exception.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var request = await ReadHeaderAsync(stream, token);
        var lines = Encoding.Latin1.GetString(request).Split("\r\n", StringSplitOptions.None);
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await DenyAsync(stream, token);
            return;
        }

        Uri? target;
        if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate("http://" + parts[1], UriKind.Absolute, out target))
            {
                await DenyAsync(stream, token);
                return;
            }
            await HandleConnectAsync(client, stream, target, token);
            return;
        }

        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out target))
        {
            var host = lines.FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
            if (host is null || !Uri.TryCreate($"http://{host}{parts[1]}", UriKind.Absolute, out target))
            {
                await DenyAsync(stream, token);
                return;
            }
        }

        if (!IsAllowed(target.Host, client))
        {
            await DenyAsync(stream, token);
            return;
        }

        using var remote = new TcpClient();
        await remote.ConnectAsync(target.Host, target.Port, token);
        var output = remote.GetStream();
        var forwarded = new List<string> { $"{parts[0]} {target.PathAndQuery} HTTP/1.1" };
        var hasConnectionHeader = false;
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            if (line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                forwarded.Add("Connection: close");
                hasConnectionHeader = true;
                continue;
            }
            if (line.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase)) continue;
            forwarded.Add(line);
        }
        if (!hasConnectionHeader) forwarded.Add("Connection: close");
        forwarded.Add("");
        forwarded.Add("");
        await output.WriteAsync(Encoding.Latin1.GetBytes(string.Join("\r\n", forwarded)), token);

        if (lines.Any(line => line.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)))
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), token);

        var contentLength = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLength is not null &&
            long.TryParse(contentLength[(contentLength.IndexOf(':') + 1)..].Trim(), out var bodyLength))
            await CopyBytesAsync(stream, output, bodyLength, token);
        else if (lines.Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("chunked", StringComparison.OrdinalIgnoreCase)))
            await CopyChunkedBodyAsync(stream, output, token);
        await RelayAsync(stream, output, token);
    }

    private async Task HandleConnectAsync(TcpClient client, NetworkStream stream, Uri target, CancellationToken token)
    {
        if (!IsAllowed(target.Host, client))
        {
            await DenyAsync(stream, token);
            return;
        }

        using var remote = new TcpClient();
        await remote.ConnectAsync(target.Host, target.Port, token);
        var remoteStream = remote.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
        await RelayAsync(stream, remoteStream, token);
    }

    private bool IsAllowed(string host, TcpClient client)
    {
        var executable = LinuxProcessInspector.GetExecutablePath(client, uid, homeDirectory);
        lock (policyLock)
            return WebsitePolicy.IsAllowed(host, executable, homeDirectory, blockedDomains, permanentDomains);
    }

    private void SaveProxySettings()
    {
        if (File.Exists(snapshotPath)) return;
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in managedSettings)
            snapshot[setting] = GSettings("get", setting);
        var temporary = snapshotPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, snapshotPath, false);
    }

    private void ApplyProxySettings()
    {
        SetIfDifferent("org.gnome.system.proxy.http host", "'127.0.0.1'");
        SetIfDifferent($"org.gnome.system.proxy.http port", Port.ToString());
        SetIfDifferent("org.gnome.system.proxy.https host", "'127.0.0.1'");
        SetIfDifferent("org.gnome.system.proxy.https port", Port.ToString());
        SetIfDifferent($"{ProxySchema} ignore-hosts", "['localhost', '127.0.0.0/8', '::1']");
        SetIfDifferent($"{ProxySchema} use-same-proxy", "true");
        SetIfDifferent($"{ProxySchema} mode", "'manual'");
        SetIfDifferent($"{ProxySchema} autoconfig-url", "''");
    }

    private void RestoreProxySettings()
    {
        var snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(snapshotPath))
            ?? throw new InvalidDataException("GNOME proxy settings backup is invalid.");

        foreach (var setting in managedSettings)
        {
            if (string.Equals(GSettings("get", setting), ExpectedValue(setting), StringComparison.Ordinal))
                GSettings("set", setting, snapshot[setting]);
            else
                Console.Error.WriteLine($"GNOME proxy setting {setting} changed outside StudyGuard; preserving that value.");
        }

        File.Delete(snapshotPath);
    }

    private static string ExpectedValue(string setting) => setting switch
    {
        $"{ProxySchema} mode" => "'manual'",
        $"{ProxySchema} autoconfig-url" => "''",
        $"{ProxySchema} ignore-hosts" => "['localhost', '127.0.0.0/8', '::1']",
        $"{ProxySchema} use-same-proxy" => "true",
        "org.gnome.system.proxy.http host" or "org.gnome.system.proxy.https host" => "'127.0.0.1'",
        "org.gnome.system.proxy.http port" or "org.gnome.system.proxy.https port" => Port.ToString(),
        _ => ""
    };

    private void SetIfDifferent(string setting, string value)
    {
        if (!string.Equals(GSettings("get", setting), value, StringComparison.Ordinal))
            GSettings("set", setting, value);
    }

    private string GSettings(string operation, string setting, string? value = null)
    {
        var parts = setting.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new InvalidOperationException($"Invalid GNOME setting: {setting}");

        var info = new ProcessStartInfo("runuser")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "--user", userName, "--", "env",
            $"HOME={homeDirectory}", $"XDG_RUNTIME_DIR=/run/user/{uid}",
            $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus",
            "gsettings", operation, parts[0], parts[1] })
            info.ArgumentList.Add(argument);
        if (value is not null) info.ArgumentList.Add(value);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start gsettings.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"gsettings {operation} {setting} timed out.");
        }
        Task.WaitAll(outputTask, errorTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"gsettings {operation} {setting} failed: {errorTask.Result.Trim()}");
        return outputTask.Result.Trim();
    }

    private static async Task<byte[]> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 32 * 1024)
        {
            var count = await stream.ReadAsync(one, token);
            if (count == 0) break;
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                var bytes = buffer.GetBuffer();
                var length = (int)buffer.Length;
                if (bytes[length - 4] == 13 && bytes[length - 3] == 10 && bytes[length - 2] == 13 && bytes[length - 1] == 10)
                    return buffer.ToArray();
            }
        }
        throw new InvalidDataException("HTTP proxy headers were incomplete or too large.");
    }

    private static async Task RelayAsync(Stream client, Stream remote, CancellationToken token)
    {
        var upload = client.CopyToAsync(remote, token);
        var download = remote.CopyToAsync(client, token);
        await Task.WhenAny(upload, download);
    }

    private static async Task CopyBytesAsync(Stream source, Stream destination, long bytesToCopy, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (bytesToCopy > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytesToCopy)), token);
            if (read == 0) throw new EndOfStreamException();
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            bytesToCopy -= read;
        }
    }

    private static async Task CopyChunkedBodyAsync(Stream source, Stream destination, CancellationToken token)
    {
        while (true)
        {
            var line = await ReadLineAsync(source, token);
            await WriteLineAsync(destination, line, token);
            if (!int.TryParse(line.Split(';', 2)[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
                throw new InvalidDataException("Invalid HTTP chunk size.");
            if (chunkSize == 0)
            {
                string trailer;
                do
                {
                    trailer = await ReadLineAsync(source, token);
                    await WriteLineAsync(destination, trailer, token);
                } while (trailer.Length > 0);
                return;
            }
            await CopyBytesAsync(source, destination, chunkSize + 2L, token);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            var count = await stream.ReadAsync(one, token);
            if (count == 0) throw new EndOfStreamException();
            buffer.WriteByte(one[0]);
            if (one[0] == 10) return Encoding.Latin1.GetString(buffer.ToArray()).TrimEnd('\r', '\n');
        }
        throw new InvalidDataException("HTTP line was too long.");
    }

    private static Task WriteLineAsync(Stream stream, string line, CancellationToken token) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n"), token).AsTask();

    private static Task DenyAsync(Stream stream, CancellationToken token)
    {
        var body = Encoding.ASCII.GetBytes("StudyGuard blocked this website.");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        return stream.WriteAsync(header.Concat(body).ToArray(), token).AsTask();
    }

    public void Dispose() => StopAndRestore();
}
