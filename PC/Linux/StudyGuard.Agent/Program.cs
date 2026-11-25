using System.Runtime.InteropServices;
using System.Text;

namespace StudyGuard.Agent;

internal static class Program
{
    private const string BeginMarker = "# BEGIN STUDYGUARD MANAGED BLOCK";
    private const string EndMarker = "# END STUDYGUARD MANAGED BLOCK";
    private static readonly HashSet<string> NeverKill = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "init", "dbus-daemon", "gnome-shell", "plasmashell", "kwin_wayland", "Xorg", "Xwayland",
        "studyguard.linux", "studyguard.agent", "studyguard-agent"
    };
    private static readonly Dictionary<int, DateTimeOffset> TermSent = [];

    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc")] private static extern uint geteuid();

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("StudyGuard.Agent can run only on Linux."); return 2; }
        if (geteuid() != 0) { Console.Error.WriteLine("Start the system agent as root (sudo or systemd)."); return 3; }
        var configPath = GetArg(args, "--config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/studyguard/config.json");
        if (!int.TryParse(GetArg(args, "--uid"), out var targetUid)) targetUid = 1000;
        Console.WriteLine($"StudyGuard Linux agent is watching user UID {targetUid}.");
        while (true)
        {
            try
            {
                var config = ConfigReader.Read(configPath);
                var now = DateTimeOffset.Now;
                var active = ConfigReader.Active(config, now);
                var blockedApps = GetBlockedApps(config, active, now);
                var blockedSites = GetBlockedSites(config, active);
                EnforceApps(blockedApps, targetUid, now);
                UpdateHosts(blockedSites);
            }
            catch (Exception exception) { Console.Error.WriteLine($"StudyGuard enforcement error: {exception.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static HashSet<string> GetBlockedApps(StudyData config, ActivityRule? active, DateTimeOffset now)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.PermanentApps)
        {
            var name = NormalizeProcess(entry.Value);
            if (name.Length > 0) blocked.Add(name);
        }
        if (active?.BlockApps == true)
        {
            var exceptions = config.ExceptionApps.Select(NormalizeProcess).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var app in active.Apps.Select(NormalizeProcess).Where(x => x.Length > 0))
                if (!exceptions.Contains(app) && !blocked.Contains(app)) blocked.Add(app);
        }
        return blocked;
    }

    private static HashSet<string> GetBlockedSites(StudyData config, ActivityRule? active)
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
                if (domain.Length > 0 && !exceptions.Any(exception => ConfigReader.Matches(domain, exception)) && !blocked.Contains(domain)) blocked.Add(domain);
            }
        return blocked;
    }

    private static void EnforceApps(HashSet<string> blocked, int uid, DateTimeOffset now)
    {
        var stillRunning = new HashSet<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                var processUid = ReadUid(Path.Combine(directory, "status"));
                if (processUid != uid) continue;
                var command = File.ReadAllBytes(Path.Combine(directory, "cmdline"));
                if (command.Length == 0) continue;
                var executable = Encoding.UTF8.GetString(command).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(executable)) continue;
                var processName = Path.GetFileName(executable);
                if (NeverKill.Contains(processName) || !blocked.Contains(processName)) continue;
                stillRunning.Add(pid);
                if (!TermSent.TryGetValue(pid, out var sentAt))
                {
                    kill(pid, 15); TermSent[pid] = now;
                    Console.WriteLine($"Sent normal close to blocked process {processName} (PID {pid}).");
                }
                else if (now - sentAt >= TimeSpan.FromSeconds(4)) kill(pid, 9);
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var pid in TermSent.Keys.Where(pid => !stillRunning.Contains(pid)).ToArray()) TermSent.Remove(pid);
    }

    private static int ReadUid(string statusPath)
    {
        foreach (var line in File.ReadLines(statusPath))
            if (line.StartsWith("Uid:", StringComparison.Ordinal) && int.TryParse(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var uid)) return uid;
        return -1;
    }

    private static void UpdateHosts(HashSet<string> domains)
    {
        const string hosts = "/etc/hosts";
        if (!File.Exists(hosts)) return;
        var existing = File.ReadAllText(hosts);
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        var inManaged = false;
        var cleaned = new List<string>();
        foreach (var line in lines)
        {
            if (line.Trim() == BeginMarker) { inManaged = true; continue; }
            if (line.Trim() == EndMarker) { inManaged = false; continue; }
            if (!inManaged) cleaned.Add(line);
        }
        var expanded = domains.SelectMany(ExpandHostNames).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (expanded.Count > 0)
        {
            while (cleaned.Count > 0 && cleaned[^1].Length == 0) cleaned.RemoveAt(cleaned.Count - 1);
            cleaned.Add(BeginMarker);
            foreach (var domain in expanded) { cleaned.Add($"0.0.0.0 {domain}"); cleaned.Add($":: {domain}"); }
            cleaned.Add(EndMarker);
        }
        var updated = string.Join('\n', cleaned).TrimEnd() + "\n";
        if (updated == existing.Replace("\r\n", "\n")) return;
        var temporary = hosts + ".studyguard.tmp";
        File.WriteAllText(temporary, updated);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, hosts, true);
        Console.WriteLine($"Updated managed website rules in {hosts} ({expanded.Count} host names).");
    }

    private static IEnumerable<string> ExpandHostNames(string domain)
    {
        yield return domain;
        yield return "www." + domain;
        yield return "m." + domain;
        yield return "mobile." + domain;
    }

    private static string NormalizeProcess(string? value)
    {
        var name = Path.GetFileName((value ?? "").Trim());
        return name.Length is > 0 and <= 120 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') ? name : "";
    }

    private static string? GetArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal)) return args[i][(key.Length + 1)..];
            if (args[i] == key && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }
}
