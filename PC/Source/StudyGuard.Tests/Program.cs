using System.Net;
using System.Net.Sockets;
using StudyGuard;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

string userYtDlp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "yt-dlp.exe");
string secondYtDlp = @"E:\youtube downloader\yt-dlp.exe";
Check(SystemWebsiteProxy.IsApprovedYtDlpPath(userYtDlp), "allow yt-dlp.exe from the user's tools folder");
Check(SystemWebsiteProxy.IsApprovedYtDlpPath(secondYtDlp), "allow yt-dlp.exe from the second configured tools folder");
Check(!SystemWebsiteProxy.IsApprovedYtDlpPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "other.exe")), "deny other executables in the user's tools folder");
Check(!SystemWebsiteProxy.IsApprovedYtDlpPath(Path.Combine(Path.GetTempPath(), "yt-dlp.exe")), "deny copies of yt-dlp from unapproved folders");
Check(SystemWebsiteProxy.IsYtDlpException("www.youtube.com", userYtDlp), "allow yt-dlp through the YouTube block");
Check(SystemWebsiteProxy.IsYtDlpException("rr2---sn-test.googlevideo.com", secondYtDlp), "allow yt-dlp to fetch YouTube video media");
Check(!SystemWebsiteProxy.IsYtDlpException("www.google.com", userYtDlp), "keep yt-dlp limited to YouTube download hosts");
Check(!SystemWebsiteProxy.IsYtDlpException("www.youtube.com", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "other.exe")), "keep YouTube blocked for other programs");
Check(!SystemWebsiteProxy.IsYtDlpException("youtube.com.evil.example", userYtDlp), "reject lookalike YouTube hosts");

Check(!SystemWebsiteProxy.IsHostAllowedByPolicy("www.youtube.com", new[] { "youtube.com" }, new[] { "youtube.com" }, new[] { "youtube.com" }, false, Array.Empty<string>()),
    "permanent website blocks keep priority over general exceptions for browsers");
Check(!SystemWebsiteProxy.IsHostAllowedByPolicy("www.youtube.com", new[] { "youtube.com" }, Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>()),
    "scheduled YouTube blocking remains active for browsers");
Check(SystemWebsiteProxy.IsHostAllowedByPolicy("youtube.com", new[] { "youtube.com" }, Array.Empty<string>(), new[] { "youtube.com" }, false, Array.Empty<string>()),
    "existing general website exceptions continue to work");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
using var client = new TcpClient();
Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
await client.ConnectAsync(IPAddress.Loopback, listenPort);
using TcpClient accepted = await accepting;
int? owner = SystemWebsiteProxy.FindTcpOwnerProcessId(
    (ushort)((IPEndPoint)client.Client.LocalEndPoint!).Port,
    (ushort)((IPEndPoint)accepted.Client.LocalEndPoint!).Port);
Check(owner == Environment.ProcessId, "identify the process that made a proxied TCP connection");
listener.Stop();

Console.WriteLine($"All {passed} StudyGuard website proxy checks passed.");
