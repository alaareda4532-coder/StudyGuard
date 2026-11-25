using System.Net;
using System.Net.Sockets;
using StudyGuard.Agent;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

var home = Path.Combine(Path.GetTempPath(), "studyguard-tests-" + Guid.NewGuid().ToString("N"));
var approvedPath = Path.Combine(home, ".local/bin/yt-dlp");
Directory.CreateDirectory(Path.GetDirectoryName(approvedPath)!);
File.WriteAllText(approvedPath, "#!/usr/bin/env python3\nimport socket, sys, time\nconnection = socket.create_connection(('127.0.0.1', int(sys.argv[1])))\ntime.sleep(20)\n");
if (OperatingSystem.IsLinux())
    File.SetUnixFileMode(approvedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
try
{
    Check(WebsitePolicy.IsYouTubeHost("www.youtube.com"), "recognize YouTube subdomains");
    Check(WebsitePolicy.IsYouTubeHost("youtubei.googleapis.com"), "recognize YouTube API host");
    Check(WebsitePolicy.IsYouTubeHost("rr2---sn-test.googlevideo.com"), "recognize YouTube media hosts");
    Check(WebsitePolicy.IsYouTubeHost("googlevideo.com"), "recognize the YouTube media parent domain");
    Check(WebsitePolicy.IsYouTubeHost("youtu.be"), "recognize YouTube short links");
    Check(!WebsitePolicy.IsYouTubeHost("youtube.com.evil.example"), "reject lookalike YouTube hosts");
    Check(!WebsitePolicy.IsYouTubeHost("www.google.com"), "reject unrelated Google hosts");

    Check(WebsitePolicy.IsAllowed("www.youtube.com", approvedPath, home, ["youtube.com"], []),
        "allow approved yt-dlp to YouTube while scheduled block is active");
    Check(!WebsitePolicy.IsAllowed("www.youtube.com", "/usr/bin/firefox", home, ["youtube.com"], []),
        "keep YouTube blocked for an unapproved browser executable");
    Check(!WebsitePolicy.IsAllowed("rr2---sn-test.googlevideo.com", "/usr/bin/curl", home, ["youtube.com"], []),
        "deny other executables from YouTube media hosts");
    Check(WebsitePolicy.IsAllowed("www.youtube.com", approvedPath, home, ["youtube.com"], ["youtube.com"]),
        "allow approved yt-dlp through permanent YouTube blocking");
    Check(!WebsitePolicy.IsAllowed("www.example.com", approvedPath, home, ["example.com"], []),
        "keep other scheduled website blocks active for yt-dlp");
    Check(!WebsitePolicy.IsAllowed("www.example.com", approvedPath, home, [], ["example.com"]),
        "keep other permanent website blocks active for yt-dlp");
    Check(WebsitePolicy.IsAllowed("www.example.com", approvedPath, home, [], []),
        "leave unblocked non-YouTube websites accessible");
    Check(!WebsitePolicy.IsAllowed("youtube.com.evil.example", approvedPath, home, ["youtube.com.evil.example"], []),
        "do not extend the YouTube exception to lookalike domains");
    Check(!WebsitePolicy.IsAllowed("www.google.com", approvedPath, home, ["google.com"], []),
        "limit the yt-dlp exception to the listed YouTube host family");
    Check(!WebsitePolicy.IsApprovedYtDlpPath(Path.Combine(home, "tools/yt-dlp"), home),
        "reject yt-dlp copies outside the approved executable path");
    var nonExecutableHome = Path.Combine(home, "nonexec");
    var nonExecutablePath = Path.Combine(nonExecutableHome, ".local/bin/yt-dlp");
    Directory.CreateDirectory(Path.GetDirectoryName(nonExecutablePath)!);
    File.WriteAllText(nonExecutablePath, "not executable");
    Check(!WebsitePolicy.IsApprovedYtDlpPath(nonExecutablePath, nonExecutableHome),
        "require the approved yt-dlp path to be executable");

    var config = new StudyData
    {
        PermanentSites = [new TimedEntry { Value = "youtube.com" }],
        ExceptionSites = ["example.com"]
    };
    var activeRule = new ActivityRule
    {
        BlockSites = true,
        Sites = ["youtube.com", "example.com", "scheduled.example"]
    };
    var activeBlocked = WebsitePolicy.GetBlockedSites(config, activeRule);
    Check(activeBlocked.Contains("youtube.com"), "retain permanent YouTube blocking during a scheduled activity");
    Check(activeBlocked.Contains("scheduled.example"), "apply other scheduled website blocks");
    Check(!activeBlocked.Contains("example.com"), "preserve scheduled website exceptions");
    Check(WebsitePolicy.GetBlockedSites(config, null).SetEquals(["youtube.com"]),
        "retain permanent site blocking when no activity is active");

    var hosts = WebsitePolicy.GetHostsDomains(["youtube.com", "www.youtube.com", "example.com"], true).ToArray();
    Check(hosts.SequenceEqual(["example.com"]), "remove YouTube only when the process-aware proxy is active");
    Check(WebsitePolicy.GetHostsDomains(["youtube.com", "example.com"], false).SequenceEqual(["youtube.com", "example.com"]),
        "retain fail-closed hosts rules when proxy setup is unavailable");

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var client = new TcpClient();
    var accepting = listener.AcceptTcpClientAsync();
    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
    using var accepted = await accepting;
    var uid = int.Parse(File.ReadLines("/proc/self/status")
        .First(line => line.StartsWith("Uid:", StringComparison.Ordinal))
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1]);
    var executable = LinuxProcessInspector.GetExecutablePath(accepted, uid, home);
    Check(!string.IsNullOrWhiteSpace(executable), "identify the owning process through proc TCP inode and file descriptor");
    Check(!WebsitePolicy.IsAllowed("www.youtube.com", executable, home, ["youtube.com"], []),
        "identified non-yt-dlp client remains blocked from YouTube");

    using var ytListener = new TcpListener(IPAddress.Loopback, 0);
    ytListener.Start();
    var ytDlp = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(approvedPath)
    {
        UseShellExecute = false,
        ArgumentList = { ((IPEndPoint)ytListener.LocalEndpoint).Port.ToString() }
    }) ?? throw new InvalidOperationException("Could not start the approved yt-dlp test executable.");
    try
    {
        using var ytConnection = await ytListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var ytExecutable = LinuxProcessInspector.GetExecutablePath(ytConnection, uid, home);
        Check(string.Equals(ytExecutable, approvedPath, StringComparison.Ordinal),
            "resolve the approved yt-dlp script path from its owning Python process");
        Check(WebsitePolicy.IsAllowed("www.youtube.com", ytExecutable, home, ["youtube.com"], []),
            "allow the identified yt-dlp process through the YouTube policy");
        Check(!WebsitePolicy.IsAllowed("www.google.com", ytExecutable, home, ["youtube.com", "google.com"], []),
            "deny the identified yt-dlp process to other blocked domains");
    }
    finally
    {
        if (!ytDlp.HasExited) ytDlp.Kill(entireProcessTree: true);
        ytDlp.WaitForExit();
    }
}
finally
{
    Directory.Delete(home, recursive: true);
}

Console.WriteLine($"All {passed} Linux website proxy checks passed.");
