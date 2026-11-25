using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyGuard.Linux;

public sealed class StudyData
{
    public List<ActivityRule> Activities { get; set; } = [];
    public List<TimedEntry> PermanentApps { get; set; } = [];
    public List<TimedEntry> PermanentSites { get; set; } = [];
    public List<string> ExceptionApps { get; set; } = [];
    public List<string> ExceptionSites { get; set; } = [];
}

public sealed class ActivityRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "نشاط جديد";
    public List<int> Days { get; set; } = [];
    public string Start { get; set; } = "07:00";
    public string End { get; set; } = "08:00";
    public bool BlockApps { get; set; } = true;
    public bool BlockSites { get; set; }
    public List<string> Apps { get; set; } = [];
    public List<string> Sites { get; set; } = [];
}

public sealed class TimedEntry
{
    public string Value { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StudyStore
{
    public static readonly TimeSpan PermanentLock = TimeSpan.FromDays(15);
    public static readonly string[] DayNames = ["الأحد", "الإثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object sync = new();
    public string FilePath { get; }

    public StudyStore()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot)) configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var directory = Path.Combine(configRoot, "studyguard");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "config.json");
    }

    public StudyData Load()
    {
        lock (sync)
        {
            try
            {
                if (!File.Exists(FilePath)) return new StudyData();
                return JsonSerializer.Deserialize<StudyData>(File.ReadAllText(FilePath), JsonOptions) ?? new StudyData();
            }
            catch { return new StudyData(); }
        }
    }

    public void Save(StudyData data)
    {
        lock (sync)
        {
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temporary, FilePath, true);
            if (OperatingSystem.IsLinux())
                try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
    }

    public static ActivityRule? Active(StudyData data, DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        return data.Activities
            .Where(rule => rule.Days.Count > 0 && TimeSpan.TryParse(rule.Start, out _) && TimeSpan.TryParse(rule.End, out _))
            .Where(rule => IsActiveOnDate(rule, local.Date, local.TimeOfDay) || IsActiveOnDate(rule, local.Date.AddDays(-1), local.TimeOfDay))
            .OrderByDescending(rule => StartFor(rule, local))
            .FirstOrDefault();
    }

    private static bool IsActiveOnDate(ActivityRule rule, DateTime date, TimeSpan now)
    {
        if (!rule.Days.Contains((int)date.DayOfWeek)) return false;
        var start = TimeSpan.Parse(rule.Start); var end = TimeSpan.Parse(rule.End);
        return start < end ? now >= start && now < end : start > end && (now >= start || now < end);
    }

    private static DateTime StartFor(ActivityRule rule, DateTime local)
    {
        var start = TimeSpan.Parse(rule.Start);
        return local.TimeOfDay < start ? local.Date.AddDays(-1).Add(start) : local.Date.Add(start);
    }

    public static string NormalizeDomain(string? value)
    {
        var domain = (value ?? "").Trim().ToLowerInvariant();
        if (Uri.TryCreate(domain.Contains("://", StringComparison.Ordinal) ? domain : "https://" + domain, UriKind.Absolute, out var uri)) domain = uri.Host;
        domain = domain.TrimEnd('.').TrimStart('.');
        return domain.Length <= 253 && domain.Split('.').All(part => part.Length is > 0 and <= 63 && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) ? domain : "";
    }

    public static bool MatchesDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
