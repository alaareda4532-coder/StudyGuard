using System.Text.Json;

namespace StudyGuard.Agent;

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
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<int> Days { get; set; } = [];
    public string Start { get; set; } = "07:00";
    public string End { get; set; } = "08:00";
    public bool BlockApps { get; set; }
    public bool BlockSites { get; set; }
    public List<string> Apps { get; set; } = [];
    public List<string> Sites { get; set; } = [];
}

public sealed class TimedEntry
{
    public string Value { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

internal static class ConfigReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static StudyData Read(string path)
    {
        try { return JsonSerializer.Deserialize<StudyData>(File.ReadAllText(path), Options) ?? new StudyData(); }
        catch (FileNotFoundException) { return new StudyData(); }
        catch (Exception exception) { Console.Error.WriteLine($"StudyGuard config: {exception.Message}"); return new StudyData(); }
    }

    public static ActivityRule? Active(StudyData data, DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        return data.Activities.Where(rule => rule.Days.Count > 0 && TimeSpan.TryParse(rule.Start, out _) && TimeSpan.TryParse(rule.End, out _))
            .Where(rule => IsActiveOnDate(rule, local.Date, local.TimeOfDay) || IsActiveOnDate(rule, local.Date.AddDays(-1), local.TimeOfDay))
            .OrderByDescending(rule => StartFor(rule, local)).FirstOrDefault();
    }

    private static bool IsActiveOnDate(ActivityRule rule, DateTime date, TimeSpan now)
    {
        if (!rule.Days.Contains((int)date.DayOfWeek)) return false;
        var start = TimeSpan.Parse(rule.Start); var end = TimeSpan.Parse(rule.End);
        return start < end ? now >= start && now < end : start > end && (now >= start || now < end);
    }

    private static DateTime StartFor(ActivityRule rule, DateTime local)
    {
        var start = TimeSpan.Parse(rule.Start); return local.TimeOfDay < start ? local.Date.AddDays(-1).Add(start) : local.Date.Add(start);
    }

    public static string NormalizeDomain(string? value)
    {
        var domain = (value ?? "").Trim().ToLowerInvariant().TrimEnd('.');
        if (Uri.TryCreate(domain.Contains("://", StringComparison.Ordinal) ? domain : "https://" + domain, UriKind.Absolute, out var uri)) domain = uri.Host;
        return domain.Length <= 253 && domain.Split('.').All(part => part.Length is > 0 and <= 63 && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) ? domain : "";
    }

    public static bool Matches(string host, string rule) => host.Equals(rule, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase);
}
