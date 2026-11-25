using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StudyGuard;

internal sealed record PrayerCity(string Key, string Name, double Latitude, double Longitude)
{
    public static IReadOnlyList<PrayerCity> EgyptianCities { get; } = new[]
    {
        new PrayerCity("beni-suef", "بني سويف", 29.0661, 31.0994),
        new PrayerCity("cairo", "القاهرة", 30.0444, 31.2357),
        new PrayerCity("giza", "الجيزة", 30.0131, 31.2089),
        new PrayerCity("alexandria", "الإسكندرية", 31.2001, 29.9187),
        new PrayerCity("mansoura", "المنصورة", 31.0409, 31.3785),
        new PrayerCity("tanta", "طنطا", 30.7865, 31.0004),
        new PrayerCity("port-said", "بورسعيد", 31.2653, 32.3019),
        new PrayerCity("suez", "السويس", 29.9668, 32.5498),
        new PrayerCity("ismailia", "الإسماعيلية", 30.5965, 32.2715),
        new PrayerCity("damietta", "دمياط", 31.4175, 31.8144),
        new PrayerCity("damanhur", "دمنهور", 31.0341, 30.4682),
        new PrayerCity("faiyum", "الفيوم", 29.3084, 30.8428),
        new PrayerCity("minya", "المنيا", 28.1099, 30.7503),
        new PrayerCity("assiut", "أسيوط", 27.1809, 31.1837),
        new PrayerCity("sohag", "سوهاج", 26.5591, 31.6957),
        new PrayerCity("qena", "قنا", 26.1551, 32.7160),
        new PrayerCity("luxor", "الأقصر", 25.6872, 32.6396),
        new PrayerCity("aswan", "أسوان", 24.0889, 32.8998),
        new PrayerCity("hurghada", "الغردقة", 27.2579, 33.8116),
        new PrayerCity("sharm-el-sheikh", "شرم الشيخ", 27.9158, 34.3300),
        new PrayerCity("marsa-matruh", "مرسى مطروح", 31.3543, 27.2373)
    };
}

internal sealed class PrayerDayTimes
{
    public TimeOnly Fajr { get; set; }
    public TimeOnly Dhuhr { get; set; }
    public TimeOnly Asr { get; set; }
    public TimeOnly Maghrib { get; set; }
    public TimeOnly Isha { get; set; }
    public DateTime FetchedAtUtc { get; set; }

    public IReadOnlyList<(string Key, string Name, TimeOnly Time)> ToPrayerList() => new[]
    {
        ("Fajr", "الفجر", Fajr),
        ("Dhuhr", "الظهر", Dhuhr),
        ("Asr", "العصر", Asr),
        ("Maghrib", "المغرب", Maghrib),
        ("Isha", "العشاء", Isha)
    };
}

internal sealed class PrayerTimesService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public async Task<PrayerDayTimes> GetTimesAsync(PrayerCity city, DateOnly date, CancellationToken cancellationToken = default)
    {
        string dateText = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        string latitude = city.Latitude.ToString(CultureInfo.InvariantCulture);
        string longitude = city.Longitude.ToString(CultureInfo.InvariantCulture);
        string url = $"https://api.aladhan.com/v1/timings/{dateText}?latitude={latitude}&longitude={longitude}&method=5&timezonestring=Africa%2FCairo&school=0";
        using HttpResponseMessage response = await Client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement timings = document.RootElement.GetProperty("data").GetProperty("timings");

        return new PrayerDayTimes
        {
            Fajr = ParseTime(timings, "Fajr"),
            Dhuhr = ParseTime(timings, "Dhuhr"),
            Asr = ParseTime(timings, "Asr"),
            Maghrib = ParseTime(timings, "Maghrib"),
            Isha = ParseTime(timings, "Isha"),
            FetchedAtUtc = DateTime.UtcNow
        };
    }

    private static TimeOnly ParseTime(JsonElement timings, string prayerName)
    {
        string value = timings.GetProperty(prayerName).GetString() ??
            throw new InvalidDataException($"وقت {prayerName} غير موجود في رد خدمة المواقيت.");
        string timeText = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!TimeOnly.TryParseExact(timeText, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out TimeOnly time))
            throw new InvalidDataException($"صيغة وقت {prayerName} غير مفهومة: {value}");
        return time;
    }
}
