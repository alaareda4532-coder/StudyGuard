
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;

namespace StudyGuard;

public partial class MainWindow : Window
{
    private readonly List<ScheduleEntry> scheduleEntries = new();

    private readonly string scheduleFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StudyGuard",
        "schedule.json");

    private readonly string settingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StudyGuard",
        "settings.json");
    private readonly SystemWebsiteProxy websiteProxy;
    private readonly PrayerTimesService prayerTimesService = new();

    private bool isRefreshing;
    private bool scheduleLoadFailed;
    private bool websiteProxyRecoveryFailed;
    private bool allowApplicationExit;
    private readonly HashSet<string> notifiedActivities = new();
    private readonly DispatcherTimer notificationTimer;
    private readonly DispatcherTimer applicationPolicyTimer;
    private System.Windows.Forms.NotifyIcon trayIcon = null!;
    private string? currentActivityName;
    private string? sleepGraceOccurrenceKey;
    private DateTime? sleepGraceUntil;
    private bool sleepShutdownRequestedThisRun;
    private bool sleepStartupRegistrationReady;
    private bool sleepStartupCleanupDone;
    private string? startupRegistrationError;
    private string? sleepEnforcementError;
    private bool prayerTimesRequestInProgress;
    private bool prayerSleepPending;
    private bool prayerSleepRequestInProgress;
    private string? prayerSleepOccurrenceKey;
    private DateTime? prayerSleepUntil;
    private DateTime nextPrayerFetchAttemptUtc;
    private DateTime nextPrayerSleepRetryUtc;
    private readonly ControlSettings controlSettings = new();

    private static readonly CultureInfo TimeCulture = CultureInfo.InvariantCulture;
    private const string SleepStartupValueName = "StudyGuardSleepGuard";
    private const string UserStartupShortcutName = "StudyGuard.lnk";
    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public MainWindow()
    {
        InitializeComponent();
        InitializeTimePickers();
        InitializeSleepGraceMinutesPicker();
        websiteProxy = new SystemWebsiteProxy(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StudyGuard",
            "internet-proxy-backup.json"));
        if (!websiteProxy.TryRecoverPreviousSettings(out string? recoveryError))
        {
            websiteProxyRecoveryFailed = true;
            MessageBox.Show(
                "تعذر استعادة إعداد الإنترنت السابق تلقائيًا؛ سيظل حجب المواقع متوقفًا لحماية اتصالك. أعد تشغيل StudyGuard لمحاولة الاستعادة مرة أخرى.\n\n" + recoveryError,
                "استعادة إعداد الإنترنت",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        InitializeTrayIcon();

        LoadSchedule();
        LoadControlSettings();
        EnsureUserStartupRegistration();
        if (!controlSettings.PrayerCityPreferenceApplied)
        {
            controlSettings.PrayerCityKey = "beni-suef";
            controlSettings.PrayerCityPreferenceApplied = true;
            SaveControlSettings();
        }
        if (!controlSettings.PrayerSleepDurationPreferenceApplied)
        {
            controlSettings.PrayerSleepMinutes = 10;
            controlSettings.PrayerSleepDurationPreferenceApplied = true;
            SaveControlSettings();
        }
        InitializePrayerSettingsControls();

        bool blockOptionMigrationNeeded = !controlSettings.BlockOptionsAutoMigrationDone;
        bool scheduleBlockOptionsChanged = false;
        if (blockOptionMigrationNeeded)
        {
            foreach (ScheduleEntry entry in scheduleEntries)
            {
                bool shouldBlockApplications = entry.BlockedApplications.Count > 0 || entry.BlockAllApplicationsExcept;
                bool shouldBlockWebsites = entry.BlockedWebsites.Count > 0 || entry.BlockAllWebsitesExcept;
                if (entry.BlockApplications != shouldBlockApplications || entry.BlockWebsites != shouldBlockWebsites)
                    scheduleBlockOptionsChanged = true;
                entry.BlockApplications = shouldBlockApplications;
                entry.BlockWebsites = shouldBlockWebsites;
            }
            controlSettings.BlockOptionsAutoMigrationDone = true;
        }

        bool previousApplicationControlEnabled = controlSettings.ApplicationControlEnabled;
        bool previousWebsiteControlEnabled = controlSettings.WebsiteControlEnabled;
        bool previousSleepShutdownEnabled = controlSettings.SleepShutdownEnabled;
        int previousSleepGraceMinutes = controlSettings.SleepGraceMinutes;
        controlSettings.ApplicationControlEnabled = scheduleEntries.Any(entry => entry.BlockApplications);
        controlSettings.WebsiteControlEnabled = !websiteProxyRecoveryFailed &&
            scheduleEntries.Any(entry => entry.BlockWebsites);
        controlSettings.SleepShutdownEnabled = true;
        controlSettings.SleepGraceMinutes = Math.Clamp(controlSettings.SleepGraceMinutes, 1, 30);
        SleepGraceMinutesComboBox.SelectedValue = controlSettings.SleepGraceMinutes;

        bool scheduleMigrationSaved = !scheduleBlockOptionsChanged || SaveSchedule();
        if (blockOptionMigrationNeeded && !scheduleMigrationSaved)
            controlSettings.BlockOptionsAutoMigrationDone = false;
        if (previousSleepShutdownEnabled != controlSettings.SleepShutdownEnabled ||
            previousSleepGraceMinutes != controlSettings.SleepGraceMinutes ||
            blockOptionMigrationNeeded ||
            previousApplicationControlEnabled != controlSettings.ApplicationControlEnabled ||
            previousWebsiteControlEnabled != controlSettings.WebsiteControlEnabled)
            SaveControlSettings();

        notificationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        notificationTimer.Tick += (_, _) =>
        {
            CheckUpcomingActivityNotifications();
            if (PermanentWebsitesPage.Visibility == Visibility.Visible)
                UpdatePermanentWebsiteRemovalState();
            if (HomePage.Visibility == Visibility.Visible)
                RefreshHomeSummary();
        };
        notificationTimer.Start();
        Closed += (_, _) => notificationTimer.Stop();

        applicationPolicyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        applicationPolicyTimer.Tick += (_, _) =>
        {
            ApplyApplicationPolicy();
            ApplyPrayerSleepPolicy(DateTime.Now);
            if (controlSettings.PrayerSleepEnabled)
                _ = RefreshPrayerTimesAsync(force: false);
        };
        applicationPolicyTimer.Start();
        Closing += MainWindow_Closing;
        System.Windows.Application.Current.SessionEnding += MainWindow_SessionEnding;
        Closed += (_, _) =>
        {
            applicationPolicyTimer.Stop();
            try
            {
                websiteProxy.StopAndRestore();
            }
            catch
            {
                // Keep the backup file so the next launch can retry recovery.
            }
            trayIcon.Visible = false;
            trayIcon.Dispose();
        };

        ShowPage(HomePage);
        RefreshHomeSummary();
        SelectTodayInSchedule();
        RefreshScheduleList();
        CheckUpcomingActivityNotifications();
        ApplyApplicationPolicy();
        ApplyPrayerSleepPolicy(DateTime.Now);
        if (controlSettings.PrayerSleepEnabled)
            _ = RefreshPrayerTimesAsync(force: false);
    }

    private void InitializeTrayIcon()
    {
        var trayMenu = new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add("فتح StudyGuard", null, (_, _) => ShowMainWindow());
        trayMenu.Items.Add("إعادة تشغيل StudyGuard لتطبيق التحديثات", null, (_, _) => RestartApplication());
        trayMenu.Items.Add("إنهاء StudyGuard", null, (_, _) => ExitApplication());

        trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "StudyGuard",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (allowApplicationExit)
            return;

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        trayIcon.ShowBalloonTip(
            2500,
            "StudyGuard يعمل بالخلفية",
            "سيستمر تطبيق الجدول والحجب. شغّل StudyGuard مرة أخرى لإظهار النافذة، أو أنهِه من أيقونة شريط المهام.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void MainWindow_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        allowApplicationExit = true;
        TryRestoreWebsiteProxy();
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        if (controlSettings.PermanentWebsiteBlocks.Count > 0)
        {
            MessageBox.Show(
                "يوجد مواقع في الحجب الدائم؛ لذلك لن يُنهى StudyGuard من قائمة الخروج حتى يستمر الحجب. بعد مرور 15 يومًا على كل موقع، أزله من صفحة الحجب الدائم إذا أردت إنهاء البرنامج.",
                "الحجب الدائم مفعّل",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (controlSettings.ApplicationControlEnabled || controlSettings.WebsiteControlEnabled || controlSettings.SleepShutdownEnabled)
        {
            MessageBoxResult result = MessageBox.Show(
                "إنهاء StudyGuard سيوقف المراقبة الحالية ويستعيد إعداد الإنترنت السابق. هل تريد المتابعة؟",
                "إنهاء StudyGuard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        try
        {
            websiteProxy.StopAndRestore();
        }
        catch (Exception ex)
        {
            websiteProxyRecoveryFailed = true;
            MessageBox.Show(
                "لم يتم إنهاء StudyGuard حتى لا يترك إعداد الإنترنت دون استعادة. أعد تشغيل التطبيق وحاول مرة أخرى.\n\n" + ex.Message,
                "تعذر إنهاء StudyGuard بأمان",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        allowApplicationExit = true;
        Close();
    }

    private void RestartApplication()
    {
        if (controlSettings.ApplicationControlEnabled || controlSettings.WebsiteControlEnabled ||
            controlSettings.SleepShutdownEnabled || controlSettings.PermanentWebsiteBlocks.Count > 0)
        {
            MessageBoxResult result = MessageBox.Show(
                "سيعيد StudyGuard تشغيل نفسه لتطبيق التحديث. سيُستعاد إعداد الإنترنت السابق أثناء إعادة التشغيل، ثم يعود الحجب عند تشغيل النسخة الجديدة. هل تريد المتابعة؟",
                "إعادة تشغيل StudyGuard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        if (!TryRestoreWebsiteProxy())
        {
            MessageBox.Show(
                "لم تتم إعادة التشغيل لأن إعداد الإنترنت لم يُستعد بأمان. أعد المحاولة بعد معالجة رسالة الخطأ الظاهرة في الصفحة الرئيسية.",
                "تعذر إعادة التشغيل بأمان",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ((App)System.Windows.Application.Current).RequestRestart();
        allowApplicationExit = true;
        Close();
    }

    // التنقل بين صفحات البرنامج
    private void ShowPage(System.Windows.Controls.Control page)
    {
        HomePage.Visibility = Visibility.Collapsed;
        SchedulePage.Visibility = Visibility.Collapsed;
        ExceptionsPage.Visibility = Visibility.Collapsed;
        PermanentWebsitesPage.Visibility = Visibility.Collapsed;
        PrayerSettingsPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        page.Visibility = Visibility.Visible;
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(HomePage);
        RefreshHomeSummary();
    }

    private void ScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        SelectTodayInSchedule();
        ShowPage(SchedulePage);
        RefreshScheduleList();
    }

    private void SelectTodayInSchedule()
    {
        string todayTag = GetDayTag(DateTime.Now.DayOfWeek);
        ComboBoxItem? todayItem = ScheduleDayComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(), todayTag, StringComparison.Ordinal));
        if (todayItem is not null)
            ScheduleDayComboBox.SelectedItem = todayItem;
    }

    private void ExceptionsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ExceptionsPage);
        RefreshExceptionLists();
    }

    private void PrayerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(PrayerSettingsPage);
        UpdatePrayerTimesSummary();
        _ = RefreshPrayerTimesAsync(force: false);
    }

    private void PermanentWebsitesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshPermanentWebsiteBlocks();
        ShowPage(PermanentWebsitesPage);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage);
        StartupStatusText.Text = startupRegistrationError is null
            ? "يبدأ StudyGuard تلقائيًا عند تسجيل الدخول إلى Windows."
            : $"تعذر تفعيل بدء التشغيل: {startupRegistrationError}";
    }

    // عرض ملخص الجدول الفعلي لليوم، بما في ذلك نشاط يعبر منتصف الليل.
    private void RefreshHomeSummary()
    {
        DateTime now = DateTime.Now;
        string todayTag = GetDayTag(now.DayOfWeek);
        var todayEntries = scheduleEntries
            .Where(entry => entry.DayTag == todayTag)
            .ToList();

        HomeDateText.Text = now.ToString(
            "dddd، dd MMMM yyyy",
            CultureInfo.GetCultureInfo("ar-EG"));
        HomeTodaySummary.Text = $"جدول اليوم يحتوي على {todayEntries.Count} نشاطًا.";
        bool hasPermanentWebsiteBlocks = controlSettings.PermanentWebsiteBlocks.Count > 0;
        string permanentWebsiteSummary = hasPermanentWebsiteBlocks
            ? $"المواقع: {controlSettings.PermanentWebsiteBlocks.Count} محجوب دائمًا."
            : "";

        ActiveScheduleActivity? active = FindActiveScheduleActivity(now);
        if (active is not null)
        {
            ScheduleEntry entry = active.Entry;
            DateTime endsAt = active.StartsAt.Date.Add(entry.EndTime.ToTimeSpan());
            if (endsAt <= active.StartsAt)
                endsAt = endsAt.AddDays(1);

            HomeCurrentActivityText.Text = entry.ActivityName;
            HomeCurrentTimeText.Text = $"الفترة: {FormatTime(entry.StartTime)} – {FormatTime(entry.EndTime)}";
            HomeTimeRemainingText.Text = controlSettings.SleepShutdownEnabled && IsSleepActivity(entry) && sleepGraceUntil is not null
                ? $"مهلة الجلسة: {FormatRemaining(sleepGraceUntil.Value - now)} قبل طلب الإيقاف"
                : $"متبقي {FormatRemaining(endsAt - now)} على نهاية النشاط";

            var policyLines = new List<string>();
            if (hasPermanentWebsiteBlocks)
                policyLines.Add(permanentWebsiteSummary);
            if (controlSettings.ApplicationControlEnabled && entry.BlockApplications)
            {
                int blockedCount = entry.BlockAllApplicationsExcept
                    ? entry.AllowedApplicationsDuringActivity.Count
                    : entry.BlockedApplications
                    .Except(controlSettings.ExceptionApplications, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                policyLines.Add(entry.BlockAllApplicationsExcept
                    ? $"التطبيقات: الكل محظور عدا {blockedCount} تطبيقات مسموحة."
                    : $"التطبيقات: {blockedCount} محظور في هذه الفترة.");
            }
            if (controlSettings.WebsiteControlEnabled && entry.BlockWebsites)
            {
                int count = entry.BlockAllWebsitesExcept
                    ? entry.AllowedWebsitesDuringActivity.Count
                    : entry.BlockedWebsites
                    .Except(controlSettings.ExceptionWebsites, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                policyLines.Add(entry.BlockAllWebsitesExcept
                    ? $"المواقع: كل المواقع محظورة عدا {count} نطاقات مسموحة."
                    : $"المواقع: {count} نطاق محظور في هذه الفترة.");
            }
            HomeRestrictionSummaryText.Text = policyLines.Count == 0
                ? "حجب التطبيقات والمواقع غير مفعّل في هذه الفترة."
                : string.Join("\n", policyLines);
            HomeBlockedSummaryText.Text = policyLines.Count == 0
                ? "لا توجد قيود من StudyGuard حاليًا."
                : hasPermanentWebsiteBlocks
                    ? "المواقع في قائمة الحجب الدائم محجوبة طوال الوقت، حتى لو كانت ضمن الاستثناءات."
                    : "المحظور هو ما أضفته لقوائم هذه الفترة فقط؛ الاستثناءات تظل مسموحة.";
        }
        else
        {
            HomeCurrentActivityText.Text = "لا يوجد نشاط جارٍ";
            HomeCurrentTimeText.Text = "الجهاز خارج فترات الجدول الآن.";
            HomeTimeRemainingText.Text = "استخدام حر";
            HomeRestrictionSummaryText.Text = hasPermanentWebsiteBlocks
                ? $"{permanentWebsiteSummary}\nقوائم الأنشطة لا تُطبّق خارج موعدها."
                : "لا تُطبّق قوائم الأنشطة خارج موعدها.";
            HomeBlockedSummaryText.Text = "لا توجد قيود مجدولة حاليًا.";
            if (hasPermanentWebsiteBlocks)
                HomeBlockedSummaryText.Text = "المواقع في قائمة الحجب الدائم تظل محجوبة خارج مواعيد الجدول.";
        }

        var next = Enumerable.Range(0, 8)
            .SelectMany(offset =>
            {
                DateTime date = now.Date.AddDays(offset);
                string dayTag = GetDayTag(date.DayOfWeek);
                return scheduleEntries.Where(entry => entry.DayTag == dayTag)
                    .Select(entry => new { Entry = entry, StartsAt = date.Add(entry.StartTime.ToTimeSpan()) });
            })
            .Where(item => item.StartsAt > now)
            .OrderBy(item => item.StartsAt)
            .FirstOrDefault();
        HomeNextActivity.Text = next is null
            ? "لا توجد أنشطة قادمة في الأيام السبعة المقبلة."
            : $"النشاط التالي: {next.Entry.ActivityName} — {next.Entry.DayName}، {FormatTime(next.Entry.StartTime)}";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        int hours = (int)remaining.TotalHours;
        return hours > 0
            ? $"{hours} ساعة و{remaining.Minutes} دقيقة"
            : $"{Math.Max(1, remaining.Minutes)} دقيقة";
    }

    private static string GetDayTag(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Saturday => "Saturday",
            DayOfWeek.Sunday => "Sunday",
            DayOfWeek.Monday => "Monday",
            DayOfWeek.Tuesday => "Tuesday",
            DayOfWeek.Wednesday => "Wednesday",
            DayOfWeek.Thursday => "Thursday",
            DayOfWeek.Friday => "Friday",
            _ => "Saturday"
        };
    }

    private void LoadControlSettings()
    {
        if (!File.Exists(settingsFilePath))
            return;

        try
        {
            ControlSettings? savedSettings = JsonSerializer.Deserialize<ControlSettings>(
                File.ReadAllText(settingsFilePath));

            if (savedSettings is not null)
            {
                controlSettings.ApplicationControlEnabled = savedSettings.ApplicationControlEnabled;
                controlSettings.WebsiteControlEnabled = savedSettings.WebsiteControlEnabled;
                controlSettings.ExceptionApplications = savedSettings.ExceptionApplications ?? new List<string>();
                controlSettings.ExceptionWebsites = savedSettings.ExceptionWebsites ?? new List<string>();
                controlSettings.PermanentWebsiteBlocks = (savedSettings.PermanentWebsiteBlocks ?? new List<PermanentWebsiteBlock>())
                    .Where(block => block is not null && TryNormalizeWebsiteHost(block.Domain, out _))
                    .ToList();
                controlSettings.SleepShutdownEnabled = savedSettings.SleepShutdownEnabled;
                controlSettings.SleepGraceMinutes = savedSettings.SleepGraceMinutes;
                controlSettings.BlockOptionsAutoMigrationDone = savedSettings.BlockOptionsAutoMigrationDone;
                controlSettings.LastSleepShutdownKey = savedSettings.LastSleepShutdownKey ?? "";
                controlSettings.PrayerSleepEnabled = savedSettings.PrayerSleepEnabled;
                controlSettings.PrayerSleepMinutes = Math.Clamp(savedSettings.PrayerSleepMinutes, 1, 180);
                controlSettings.PrayerSleepDurationPreferenceApplied = savedSettings.PrayerSleepDurationPreferenceApplied;
                controlSettings.PrayerCityKey = savedSettings.PrayerCityKey ?? "beni-suef";
                controlSettings.PrayerCityPreferenceApplied = savedSettings.PrayerCityPreferenceApplied;
                controlSettings.EnabledPrayers = savedSettings.EnabledPrayers ?? new List<string> { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };
                controlSettings.LastPrayerNotificationKey = savedSettings.LastPrayerNotificationKey ?? "";
                controlSettings.PrayerTimesCache = savedSettings.PrayerTimesCache ?? new Dictionary<string, PrayerDayTimes>();
            }
        }
        catch (Exception ex)
        {
            websiteProxyRecoveryFailed = true;
            MessageBox.Show(
                "تعذر تحميل الاستثناءات؛ سيظل الحجب متوقفًا لحماية جهازك.\n\n" + ex.Message,
                "تعذر تحميل البيانات",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool SaveControlSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
            WriteFileAtomically(
                settingsFilePath,
                JsonSerializer.Serialize(controlSettings, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "تعذر حفظ إعدادات التحكم.\n\n" + ex.Message,
                "خطأ في حفظ الإعدادات",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void ApplyApplicationPolicy()
    {
        DateTime now = DateTime.Now;
        ActiveScheduleActivity? activeActivity = FindActiveScheduleActivity(now);
        currentActivityName = activeActivity?.Entry.ActivityName;
        bool sleepActivityActive = activeActivity is not null && IsSleepActivity(activeActivity.Entry);
        bool prayerSleepWindowActive = GetActivePrayerSleepOccurrence(now) is not null;
        if (activeActivity is not null && sleepActivityActive && controlSettings.SleepShutdownEnabled && !prayerSleepWindowActive)
            ApplySleepEnforcement(activeActivity, now);
        else if (!sleepActivityActive || !controlSettings.SleepShutdownEnabled)
        {
            sleepGraceOccurrenceKey = null;
            sleepGraceUntil = null;
            sleepShutdownRequestedThisRun = false;
            sleepEnforcementError = null;
            if (!sleepActivityActive || !controlSettings.SleepShutdownEnabled)
                TryRemoveSleepStartupRegistration();
        }

        bool hasPermanentWebsiteBlocks = controlSettings.PermanentWebsiteBlocks.Count > 0;
        if (!controlSettings.ApplicationControlEnabled && !controlSettings.WebsiteControlEnabled && !hasPermanentWebsiteBlocks)
        {
            if (websiteProxy.IsRunning)
                TryRestoreWebsiteProxy();
            if (sleepEnforcementError is not null)
                ScheduleStatusText.Text = sleepEnforcementError;
            return;
        }

        if (activeActivity is null)
        {
            string? permanentWebsiteError = websiteProxyRecoveryFailed
                ? "إعداد الإنترنت يحتاج إلى استعادة؛ الحجب الدائم متوقف حتى استعادة الإعداد السابق."
                : null;
            if (hasPermanentWebsiteBlocks && !websiteProxyRecoveryFailed)
            {
                try
                {
                    websiteProxy.ApplyBlockList(Array.Empty<string>(), controlSettings.ExceptionWebsites,
                        alwaysBlocked: controlSettings.PermanentWebsiteBlocks.Select(block => block.Domain));
                }
                catch (Exception ex)
                {
                    permanentWebsiteError = $"تعذر تطبيق الحجب الدائم: {ex.Message}";
                }
            }
            else if (websiteProxy.IsRunning)
            {
                TryRestoreWebsiteProxy();
            }
            ScheduleStatusText.Text = sleepEnforcementError ?? permanentWebsiteError ??
                (hasPermanentWebsiteBlocks
                    ? "الحجب الدائم للمواقع مستمر؛ لا يوجد نشاط مجدول الآن."
                    : "لا يوجد نشاط مجدول الآن؛ لا تُطبّق قوائم التحكم.");
            return;
        }

        ScheduleEntry entry = activeActivity.Entry;
        string? websiteError = websiteProxyRecoveryFailed
            ? "إعداد الإنترنت يحتاج إلى استعادة؛ حجب المواقع متوقف."
            : null;
        bool applyScheduledWebsitePolicy = controlSettings.WebsiteControlEnabled && entry.BlockWebsites;
        if ((applyScheduledWebsitePolicy || hasPermanentWebsiteBlocks) && !websiteProxyRecoveryFailed)
        {
            try
            {
                websiteProxy.ApplyBlockList(
                    applyScheduledWebsitePolicy ? entry.BlockedWebsites : Array.Empty<string>(),
                    controlSettings.ExceptionWebsites,
                    applyScheduledWebsitePolicy && entry.BlockAllWebsitesExcept,
                    applyScheduledWebsitePolicy ? entry.AllowedWebsitesDuringActivity : Array.Empty<string>(),
                    controlSettings.PermanentWebsiteBlocks.Select(block => block.Domain));
            }
            catch (Exception ex)
            {
                websiteError = $"تعذر تطبيق قائمة المواقع: {ex.Message}";
            }
        }
        else if (websiteProxy.IsRunning)
        {
            TryRestoreWebsiteProxy();
        }

        string? applicationError = controlSettings.ApplicationControlEnabled && entry.BlockApplications
            ? CloseBlockedApplications(entry)
            : null;
        ScheduleStatusText.Text = sleepEnforcementError ?? websiteError ?? applicationError ??
            (controlSettings.SleepShutdownEnabled && IsSleepActivity(entry) && sleepShutdownRequestedThisRun
                    ? $"تم طلب إيقاف الجهاز لوضع النوم؛ إذا أُلغي الطلب سيُعاد بعد {controlSettings.SleepGraceMinutes} دقائق."
                    : controlSettings.SleepShutdownEnabled && IsSleepActivity(entry) && sleepGraceUntil is not null
                        ? $"وضع النوم: متبقي {FormatRemaining(sleepGraceUntil.Value - now)} قبل طلب الإيقاف."
                        : (controlSettings.ApplicationControlEnabled && entry.BlockApplications) ||
                          applyScheduledWebsitePolicy
                ? $"الوضع الحالي: {entry.ActivityName}."
                : hasPermanentWebsiteBlocks ? "الحجب الدائم للمواقع مستمر." : "التحكم متوقف.");
    }

    private void ApplySleepEnforcement(ActiveScheduleActivity activeActivity, DateTime now)
    {
        if (!TryEnsureSleepStartupRegistration(out string? registrationError))
        {
            sleepEnforcementError = $"تعذر إعداد بدء التشغيل بعد تسجيل الدخول؛ لم يُطلب إيقاف الجهاز: {registrationError}";
            return;
        }
        sleepEnforcementError = null;

        string occurrenceKey = $"{activeActivity.Entry.Id:N}|{activeActivity.StartsAt:yyyyMMddHHmm}";
        TimeSpan timeSinceStart = now - activeActivity.StartsAt;
        if (timeSinceStart <= TimeSpan.FromMinutes(1) &&
            !string.Equals(controlSettings.LastSleepShutdownKey, occurrenceKey, StringComparison.Ordinal))
        {
            controlSettings.LastSleepShutdownKey = occurrenceKey;
            if (!SaveControlSettings())
            {
                sleepEnforcementError = "تعذر حفظ سجل وضع النوم؛ لم يُطلب إيقاف الكمبيوتر.";
                return;
            }

            RequestSleepShutdown();
            return;
        }

        if (!string.Equals(sleepGraceOccurrenceKey, occurrenceKey, StringComparison.Ordinal))
        {
            sleepGraceOccurrenceKey = occurrenceKey;
            sleepGraceUntil = now.AddMinutes(controlSettings.SleepGraceMinutes);
            sleepShutdownRequestedThisRun = false;
            string graceMessage = $"تم تشغيل الجهاز أثناء وضع النوم. لديك {controlSettings.SleepGraceMinutes} دقائق، وبعدها سيُطلب إيقافه مجددًا.";
            ScheduleStatusText.Text = graceMessage;
            trayIcon.ShowBalloonTip(8000, "مهلة وضع النوم", graceMessage, System.Windows.Forms.ToolTipIcon.Warning);
        }

        if (sleepGraceUntil is null || now < sleepGraceUntil.Value)
            return;

        if (sleepShutdownRequestedThisRun)
            sleepShutdownRequestedThisRun = false;

        RequestSleepShutdown();
        sleepGraceUntil = now.AddMinutes(controlSettings.SleepGraceMinutes);
    }

    private void RequestSleepShutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /f /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            sleepShutdownRequestedThisRun = true;
            sleepEnforcementError = null;
            ScheduleStatusText.Text = $"انتهت مهلة وضع النوم؛ طُلب إيقاف الجهاز بالقوة. سيُعاد الطلب بعد {controlSettings.SleepGraceMinutes} دقائق إذا بقي مفتوحًا.";
        }
        catch (Exception ex)
        {
            sleepShutdownRequestedThisRun = false;
            sleepEnforcementError = $"تعذر طلب إيقاف Windows: {ex.Message}";
        }
    }

    private bool TryEnsureSleepStartupRegistration(out string? error)
    {
        error = null;
        if (sleepStartupRegistrationReady)
            return true;

        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("تعذر تحديد مسار StudyGuard التنفيذي.");
            string command = $"\"{executablePath}\" --sleep-guard";
            if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string? assemblyPath = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(assemblyPath))
                    throw new InvalidOperationException("تعذر تحديد ملف StudyGuard لتشغيله بعد تسجيل الدخول.");
                command = $"\"{executablePath}\" \"{assemblyPath}\" --sleep-guard";
            }

            using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath, writable: true)
                ?? throw new InvalidOperationException("تعذر فتح إعداد بدء التشغيل لحساب Windows الحالي.");
            runKey.SetValue(SleepStartupValueName, command, RegistryValueKind.String);
            sleepStartupRegistrationReady = true;
            sleepStartupCleanupDone = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void TryRemoveSleepStartupRegistration()
    {
        if (sleepStartupCleanupDone)
            return;

        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath, writable: true);
            runKey?.DeleteValue(SleepStartupValueName, throwOnMissingValue: false);
            sleepStartupRegistrationReady = false;
            sleepStartupCleanupDone = true;
        }
        catch (Exception ex)
        {
            ScheduleStatusText.Text = $"تعذر إزالة تشغيل StudyGuard التلقائي لوضع النوم: {ex.Message}";
        }
    }

    private bool IsRegisteredToRunAtStartup()
    {
        try
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup), UserStartupShortcutName);
            return File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
    }

    private void EnsureUserStartupRegistration()
    {
        try
        {
            if (!IsRegisteredToRunAtStartup())
                CreateUserStartupShortcut();
            startupRegistrationError = null;
        }
        catch (Exception ex)
        {
            startupRegistrationError = ex.Message;
            StartupStatusText.Text = $"تعذر تفعيل بدء التشغيل: {ex.Message}";
        }
    }

    private static void CreateUserStartupShortcut()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("تعذر تحديد مسار StudyGuard التنفيذي.");
        string arguments = string.Empty;
        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new InvalidOperationException("تعذر تحديد ملف StudyGuard لتشغيله بعد تسجيل الدخول.");
            arguments = $"\"{assemblyPath}\"";
        }

        string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(startupDirectory);
        string shortcutPath = Path.Combine(startupDirectory, UserStartupShortcutName);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("تعذر إنشاء اختصار بدء تشغيل Windows.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("تعذر إنشاء اختصار بدء تشغيل Windows.");
        try
        {
            dynamic shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })
                ?? throw new InvalidOperationException("تعذر إنشاء اختصار بدء تشغيل Windows.");
            shortcut.TargetPath = executablePath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            shortcut.IconLocation = $"{executablePath},0";
            shortcut.Description = "تشغيل StudyGuard عند تسجيل الدخول";
            shortcut.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool IsSleepActivity(ScheduleEntry entry)
    {
        return entry.ActivityName.Contains("نوم", StringComparison.OrdinalIgnoreCase) ||
               entry.ActivityName.Contains("sleep", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryRestoreWebsiteProxy()
    {
        try
        {
            websiteProxy.StopAndRestore();
            websiteProxyRecoveryFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            websiteProxyRecoveryFailed = true;
            ScheduleStatusText.Text = $"تعذرت استعادة إعداد الإنترنت: {ex.Message}";
            return false;
        }
    }

    private ActiveScheduleActivity? FindActiveScheduleActivity(DateTime now)
    {
        var matchingActivities = new List<ActiveScheduleActivity>();
        foreach (DateTime day in new[] { now.Date, now.Date.AddDays(-1) })
        {
            string dayTag = GetDayTag(day.DayOfWeek);
            foreach (ScheduleEntry entry in scheduleEntries.Where(item => item.DayTag == dayTag))
            {
                DateTime startsAt = day.Add(entry.StartTime.ToTimeSpan());
                DateTime endsAt = day.Add(entry.EndTime.ToTimeSpan());
                if (entry.EndTime <= entry.StartTime)
                    endsAt = endsAt.AddDays(1);

                if (now >= startsAt && now < endsAt)
                    matchingActivities.Add(new ActiveScheduleActivity(entry, startsAt));
            }
        }

        return matchingActivities
            .OrderByDescending(item => item.StartsAt)
            .FirstOrDefault();
    }

    private string? CloseBlockedApplications(ScheduleEntry entry)
    {
        var blockedApplications = entry.BlockedApplications
            .Except(controlSettings.ExceptionApplications, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedApplications = entry.AllowedApplicationsDuringActivity
            .Concat(controlSettings.ExceptionApplications)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!entry.BlockAllApplicationsExcept && blockedApplications.Count == 0)
            return null;

        int currentSessionId = Process.GetCurrentProcess().SessionId;
        string? firstError = null;

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId ||
                        process.SessionId != currentSessionId ||
                        process.MainWindowHandle == IntPtr.Zero)
                        continue;

                    string? executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) || IsProtectedWindowsProcess(executablePath))
                        continue;

                    bool shouldClose = entry.BlockAllApplicationsExcept
                        ? !allowedApplications.Contains(executablePath)
                        : blockedApplications.Contains(executablePath);
                    if (!shouldClose)
                        continue;

                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        firstError ??= $"تعذر إنهاء {Path.GetFileName(executablePath)}: {ex.Message}";
                    }
                }
                catch
                {
                    // نتجاوز العملية إذا رفض Windows قراءتها أو إغلاقها.
                }
            }
        }
        return firstError;
    }

    private static bool IsProtectedWindowsProcess(string executablePath)
    {
        string processName = Path.GetFileNameWithoutExtension(executablePath);
        return processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("dwm", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("sihost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("ctfmon", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("LockApp", StringComparison.OrdinalIgnoreCase);
    }

    // إرسال تنبيه مرة واحدة عندما يتبقى عشر دقائق أو أقل على النشاط.
    private void CheckUpcomingActivityNotifications()
    {
        DateTime now = DateTime.Now;
        string todayTag = GetDayTag(now.DayOfWeek);

        var upcomingActivities = scheduleEntries
            .Select((entry, index) => new
            {
                Entry = entry,
                Index = index,
                StartsAt = now.Date.Add(entry.StartTime.ToTimeSpan())
            })
            .Where(item => item.Entry.DayTag == todayTag)
            .Where(item => item.StartsAt > now && item.StartsAt - now <= TimeSpan.FromMinutes(10))
            .OrderBy(item => item.StartsAt);

        foreach (var item in upcomingActivities)
        {
            string notificationKey =
                $"{now:yyyy-MM-dd}|{item.Index}|{item.Entry.DayTag}|{item.Entry.ActivityName}|{item.Entry.StartTime}";

            if (!notifiedActivities.Add(notificationKey))
                continue;

            int minutesRemaining = (int)Math.Ceiling((item.StartsAt - now).TotalMinutes);
            string notificationText = IsSleepActivity(item.Entry)
                ? $"يا بطل، فاضل {minutesRemaining} دقائق على وضع النوم. احفظ شغلك وجهّز نفسك للراحة."
                : $"تنبيه: يبدأ نشاط «{item.Entry.ActivityName}» خلال {minutesRemaining} دقائق.";
            ScheduleNotificationText.Text = notificationText;
            ScheduleNotificationBanner.Visibility = Visibility.Visible;
            trayIcon.ShowBalloonTip(
                10000,
                "StudyGuard",
                notificationText,
                System.Windows.Forms.ToolTipIcon.Info);
            break;
        }
    }

    private void DismissScheduleNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleNotificationBanner.Visibility = Visibility.Collapsed;
    }

    // تحميل الجدول المحفوظ أو إنشاء جدول افتراضي
    private void LoadSchedule()
    {
        try
        {
            if (File.Exists(scheduleFilePath))
            {
                string json = File.ReadAllText(scheduleFilePath);

                List<ScheduleEntry>? savedEntries =
                    JsonSerializer.Deserialize<List<ScheduleEntry>>(json);

                if (savedEntries is not null)
                {
                    scheduleEntries.Clear();
                    foreach (ScheduleEntry savedEntry in savedEntries)
                    {
                        savedEntry.AllowedApplications ??= new List<string>();
                        savedEntry.BlockedApplications ??= new List<string>();
                        savedEntry.BlockedWebsites ??= new List<string>();
                        savedEntry.AllowedWebsites ??= new List<string>();
                        savedEntry.AllowedApplicationsDuringActivity ??= new List<string>();
                        savedEntry.AllowedWebsitesDuringActivity ??= new List<string>();
                        if (savedEntry.Id == Guid.Empty)
                            savedEntry.Id = Guid.NewGuid();
                    }
                    scheduleEntries.AddRange(savedEntries);
                }
                else
                {
                    CreateDefaultSchedule();
                    SaveSchedule();
                }
            }
            else
            {
                CreateDefaultSchedule();
                SaveSchedule();
            }
        }
        catch (Exception ex)
        {
            scheduleLoadFailed = true;
            MessageBox.Show(
                "تعذر تحميل الجدول المحفوظ.\n\n" + ex.Message +
                "\n\nسيتم عرض جدول افتراضي مؤقتًا. عند أول حفظ، سيحتفظ التطبيق بنسخة من الملف التالف قبل استبداله.",
                "StudyGuard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            scheduleEntries.Clear();
            CreateDefaultSchedule();
        }
    }

    // إنشاء الأنشطة الافتراضية لكل يوم
    private void CreateDefaultSchedule()
    {
        scheduleEntries.Clear();

        var days = new[]
        {
            (Tag: "Saturday", Name: "السبت"),
            (Tag: "Sunday", Name: "الأحد"),
            (Tag: "Monday", Name: "الإثنين"),
            (Tag: "Tuesday", Name: "الثلاثاء"),
            (Tag: "Wednesday", Name: "الأربعاء"),
            (Tag: "Thursday", Name: "الخميس"),
            (Tag: "Friday", Name: "الجمعة")
        };

        var activities = new[]
        {
            (Name: "نوم", Start: "22:00", End: "06:00"),
            (Name: "تحضيرات وصلاة", Start: "06:00", End: "07:00"),
            (Name: "مذاكرة", Start: "07:00", End: "13:00"),
            (Name: "صلاة الظهر أو قراءة", Start: "13:00", End: "14:00"),
            (Name: "سماع محاضرات أو تعلّم شيء جديد", Start: "14:00", End: "16:00"),
            (Name: "صلاة أو قراءة", Start: "16:00", End: "17:00"),
            (Name: "برمجة", Start: "17:00", End: "19:00"),
            (Name: "أكل", Start: "19:00", End: "20:00"),
            (Name: "جلوس مع العائلة", Start: "20:00", End: "22:00")
        };

        foreach (var day in days)
        {
            foreach (var activity in activities)
            {
                scheduleEntries.Add(new ScheduleEntry
                {
                    DayTag = day.Tag,
                    DayName = day.Name,

                    ActivityName = activity.Name,

                    StartTime = TimeOnly.ParseExact(
                        activity.Start,
                        "HH:mm",
                        CultureInfo.InvariantCulture),

                    EndTime = TimeOnly.ParseExact(
                        activity.End,
                        "HH:mm",
                        CultureInfo.InvariantCulture)
                });
            }
        }
    }

    // حفظ الجدول محليًا
    private bool SaveSchedule()
    {
        try
        {
            string? directory = Path.GetDirectoryName(scheduleFilePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(
                scheduleEntries,
                new JsonSerializerOptions { WriteIndented = true });

            string? corruptedBackupPath = null;
            if (scheduleLoadFailed && File.Exists(scheduleFilePath))
            {
                corruptedBackupPath = scheduleFilePath + ".corrupt-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
                File.Copy(scheduleFilePath, corruptedBackupPath, overwrite: false);
            }

            WriteFileAtomically(scheduleFilePath, json);
            scheduleLoadFailed = false;

            if (corruptedBackupPath is not null)
            {
                MessageBox.Show(
                    "تم حفظ نسخة من ملف الجدول التالف هنا قبل استبداله:\n" + corruptedBackupPath,
                    "نسخة استعادة الجدول",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return true;
        }
        catch (Exception ex)
        {
            websiteProxyRecoveryFailed = true;
            MessageBox.Show(
                "تعذر حفظ الجدول.\n\n" + ex.Message,
                "خطأ في الحفظ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private static void WriteFileAtomically(string path, string contents)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }

    // عند تغيير اليوم
    private void ScheduleDayComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsInitialized || isRefreshing)
            return;

        RefreshScheduleList();

        if (ScheduleStatusText != null)
            ScheduleStatusText.Text = "";
    }

    private string GetSelectedDayTag()
    {
        if (ScheduleDayComboBox.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString() ?? "Saturday";
        }

        return "Saturday";
    }

    // ترتيب الأنشطة مع وضع النوم الذي يعبر منتصف الليل أولًا
    private static int GetActivityOrder(ScheduleEntry entry)
    {
        if (entry.StartTime.Hour >= 22)
            return -1;

        return entry.StartTime.Hour * 60 + entry.StartTime.Minute;
    }

    // عرض الوقت بنظام 12 ساعة
    private static string FormatTime(TimeOnly time)
    {
        return time.ToString("hh:mm tt", CultureInfo.InvariantCulture);
    }

    private void InitializeTimePickers()
    {
        foreach (ComboBox hourPicker in new[] { StartHourComboBox, EndHourComboBox })
            hourPicker.ItemsSource = Enumerable.Range(1, 12)
                .Select(hour => hour.ToString("00", CultureInfo.InvariantCulture)).ToList();
        foreach (ComboBox minutePicker in new[] { StartMinuteComboBox, EndMinuteComboBox })
            minutePicker.ItemsSource = Enumerable.Range(0, 60)
                .Select(minute => minute.ToString("00", CultureInfo.InvariantCulture)).ToList();
        foreach (ComboBox periodPicker in new[] { StartPeriodComboBox, EndPeriodComboBox })
            periodPicker.ItemsSource = new[] { "AM", "PM" };

        SetTimePicker(StartHourComboBox, StartMinuteComboBox, StartPeriodComboBox, new TimeOnly(22, 0));
        SetTimePicker(EndHourComboBox, EndMinuteComboBox, EndPeriodComboBox, new TimeOnly(6, 0));
    }

    private void InitializeSleepGraceMinutesPicker()
    {
        SleepGraceMinutesComboBox.DisplayMemberPath = nameof(SleepGraceOption.DisplayText);
        SleepGraceMinutesComboBox.SelectedValuePath = nameof(SleepGraceOption.Minutes);
        SleepGraceMinutesComboBox.ItemsSource = Enumerable.Range(1, 30)
            .Select(minutes => new SleepGraceOption(minutes,
                minutes == 1 ? "دقيقة واحدة" : $"{minutes} دقائق"))
            .ToList();
    }

    private void SaveSleepGraceMinutesButton_Click(object sender, RoutedEventArgs e)
    {
        if (SleepGraceMinutesComboBox.SelectedValue is not int minutes || minutes is < 1 or > 30)
            return;
        if (minutes == controlSettings.SleepGraceMinutes)
        {
            ScheduleStatusText.Text = $"مهلة وضع النوم مضبوطة بالفعل على {minutes} دقائق.";
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            $"بعد تشغيل الجهاز أثناء موعد النوم، سينتظر StudyGuard {minutes} دقائق ثم يطلب إيقافه. عند بداية موعد النوم نفسه سيطلب إيقاف الجهاز فورًا. هل تريد حفظ هذه المهلة؟",
            "تأكيد مهلة وضع النوم",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            SleepGraceMinutesComboBox.SelectedValue = controlSettings.SleepGraceMinutes;
            return;
        }

        int previousMinutes = controlSettings.SleepGraceMinutes;
        controlSettings.SleepGraceMinutes = minutes;
        if (!SaveControlSettings())
        {
            controlSettings.SleepGraceMinutes = previousMinutes;
            SleepGraceMinutesComboBox.SelectedValue = previousMinutes;
            return;
        }

        if (sleepGraceOccurrenceKey is not null)
            sleepGraceUntil = DateTime.Now.AddMinutes(minutes);
        ScheduleStatusText.Text = $"تم حفظ مهلة وضع النوم: {minutes} دقائق. يمكنك تغييرها من هذه القائمة عند اختيار نشاط النوم.";
        RefreshHomeSummary();
    }

    private void InitializePrayerSettingsControls()
    {
        PrayerCityComboBox.ItemsSource = PrayerCity.EgyptianCities;
        PrayerCityComboBox.SelectedValuePath = nameof(PrayerCity.Key);
        PrayerCityComboBox.DisplayMemberPath = nameof(PrayerCity.Name);
        PrayerCityComboBox.SelectedValue = PrayerCity.EgyptianCities.Any(city => city.Key == controlSettings.PrayerCityKey)
            ? controlSettings.PrayerCityKey
            : "beni-suef";

        PrayerSleepDurationComboBox.ItemsSource = new[] { 5, 10, 15, 20, 30, 45, 60, 90, 120, 180 }
            .Select(minutes => new PrayerSleepDurationOption(minutes,
                minutes == 5 ? "5 دقائق" : $"{minutes} دقيقة"))
            .ToList();
        PrayerSleepDurationComboBox.SelectedValue = controlSettings.PrayerSleepMinutes;
        PrayerSleepEnabledCheckBox.IsChecked = controlSettings.PrayerSleepEnabled;
        PrayerFajrCheckBox.IsChecked = controlSettings.EnabledPrayers.Contains("Fajr", StringComparer.OrdinalIgnoreCase);
        PrayerDhuhrCheckBox.IsChecked = controlSettings.EnabledPrayers.Contains("Dhuhr", StringComparer.OrdinalIgnoreCase);
        PrayerAsrCheckBox.IsChecked = controlSettings.EnabledPrayers.Contains("Asr", StringComparer.OrdinalIgnoreCase);
        PrayerMaghribCheckBox.IsChecked = controlSettings.EnabledPrayers.Contains("Maghrib", StringComparer.OrdinalIgnoreCase);
        PrayerIshaCheckBox.IsChecked = controlSettings.EnabledPrayers.Contains("Isha", StringComparer.OrdinalIgnoreCase);
        UpdatePrayerTimesSummary();
    }

    private void SavePrayerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = PrayerSleepEnabledCheckBox.IsChecked == true;
        string cityKey = PrayerCityComboBox.SelectedValue?.ToString() ?? "";
        if (PrayerCity.EgyptianCities.All(city => city.Key != cityKey) ||
            PrayerSleepDurationComboBox.SelectedValue is not int minutes || minutes is < 1 or > 180)
        {
            PrayerTimesStatusText.Text = "اختار مدينة ومدة سكون صحيحتين.";
            return;
        }

        var selectedPrayers = new List<string>();
        if (PrayerFajrCheckBox.IsChecked == true) selectedPrayers.Add("Fajr");
        if (PrayerDhuhrCheckBox.IsChecked == true) selectedPrayers.Add("Dhuhr");
        if (PrayerAsrCheckBox.IsChecked == true) selectedPrayers.Add("Asr");
        if (PrayerMaghribCheckBox.IsChecked == true) selectedPrayers.Add("Maghrib");
        if (PrayerIshaCheckBox.IsChecked == true) selectedPrayers.Add("Isha");
        if (enabled && selectedPrayers.Count == 0)
        {
            MessageBox.Show("اختار صلاة واحدة على الأقل قبل تفعيل السكون.", "إعدادات الصلاة",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (enabled && !controlSettings.PrayerSleepEnabled &&
            MessageBox.Show(
                $"سيظهر تنبيه عند كل صلاة اخترتها، ثم سيدخل الجهاز في السكون لمدة {minutes} دقيقة. إذا أيقظت اللابتوب قبل انتهاء المدة فسيعود للسكون. هل تريد التفعيل؟",
                "تأكيد السكون مع الصلاة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            PrayerSleepEnabledCheckBox.IsChecked = false;
            return;
        }

        bool previousEnabled = controlSettings.PrayerSleepEnabled;
        int previousMinutes = controlSettings.PrayerSleepMinutes;
        bool previousDurationPreferenceApplied = controlSettings.PrayerSleepDurationPreferenceApplied;
        string previousCity = controlSettings.PrayerCityKey;
        List<string> previousPrayers = controlSettings.EnabledPrayers;
        controlSettings.PrayerSleepEnabled = enabled;
        controlSettings.PrayerSleepMinutes = minutes;
        controlSettings.PrayerSleepDurationPreferenceApplied = true;
        controlSettings.PrayerCityKey = cityKey;
        controlSettings.EnabledPrayers = selectedPrayers;
        if (!SaveControlSettings())
        {
            controlSettings.PrayerSleepEnabled = previousEnabled;
            controlSettings.PrayerSleepMinutes = previousMinutes;
            controlSettings.PrayerSleepDurationPreferenceApplied = previousDurationPreferenceApplied;
            controlSettings.PrayerCityKey = previousCity;
            controlSettings.EnabledPrayers = previousPrayers;
            InitializePrayerSettingsControls();
            return;
        }

        prayerSleepOccurrenceKey = null;
        prayerSleepUntil = null;
        nextPrayerFetchAttemptUtc = DateTime.MinValue;
        PrayerTimesStatusText.Text = "تم حفظ إعدادات الصلاة.";
        _ = RefreshPrayerTimesAsync(force: true);
        ApplyPrayerSleepPolicy(DateTime.Now);
    }

    private async void RefreshPrayerTimesButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrayerCityComboBox.SelectedItem is PrayerCity city && city.Key != controlSettings.PrayerCityKey)
        {
            PrayerTimesStatusText.Text = "احفظ المدينة أولًا ثم حدّث المواقيت.";
            return;
        }
        await RefreshPrayerTimesAsync(force: true);
    }

    private async Task RefreshPrayerTimesAsync(bool force)
    {
        if (prayerTimesRequestInProgress)
            return;

        PrayerCity? city = PrayerCity.EgyptianCities.FirstOrDefault(item => item.Key == controlSettings.PrayerCityKey);
        if (city is null)
            return;

        DateOnly egyptDate = GetEgyptDateNow();
        string cacheKey = GetPrayerCacheKey(city.Key, egyptDate);
        if (controlSettings.PrayerTimesCache.TryGetValue(cacheKey, out PrayerDayTimes? cached) &&
            !force && DateTime.UtcNow - cached.FetchedAtUtc < TimeSpan.FromHours(12))
        {
            nextPrayerFetchAttemptUtc = cached.FetchedAtUtc.AddHours(12);
            UpdatePrayerTimesSummary();
            return;
        }
        if (!force && DateTime.UtcNow < nextPrayerFetchAttemptUtc)
            return;

        prayerTimesRequestInProgress = true;
        PrayerTimesStatusText.Text = $"جارٍ جلب مواقيت اليوم لمدينة {city.Name}…";
        try
        {
            PrayerDayTimes dayTimes = await prayerTimesService.GetTimesAsync(city, egyptDate);
            controlSettings.PrayerTimesCache[cacheKey] = dayTimes;
            nextPrayerFetchAttemptUtc = DateTime.UtcNow.AddHours(12);
            if (controlSettings.PrayerTimesCache.Count > 45)
            {
                foreach (string oldKey in controlSettings.PrayerTimesCache
                             .OrderByDescending(pair => pair.Value.FetchedAtUtc)
                             .Skip(35).Select(pair => pair.Key).ToList())
                    controlSettings.PrayerTimesCache.Remove(oldKey);
            }

            if (SaveControlSettings())
            {
                PrayerTimesStatusText.Text = $"تم تحديث مواقيت اليوم لمدينة {city.Name} وفق طريقة الهيئة المصرية العامة للمساحة.";
                UpdatePrayerTimesSummary();
                ApplyPrayerSleepPolicy(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            nextPrayerFetchAttemptUtc = DateTime.UtcNow.AddMinutes(15);
            UpdatePrayerTimesSummary();
            PrayerTimesStatusText.Text = controlSettings.PrayerTimesCache.ContainsKey(cacheKey)
                ? $"تعذر تحديث المواقيت الآن؛ سأستخدم النسخة المحفوظة. {ex.Message}"
                : $"تعذر جلب مواقيت اليوم. تأكد من اتصال الإنترنت ثم اضغط «تحديث مواقيت اليوم». {ex.Message}";
        }
        finally
        {
            prayerTimesRequestInProgress = false;
        }
    }

    private void UpdatePrayerTimesSummary()
    {
        if (PrayerTimesSummaryText is null)
            return;

        PrayerCity? city = PrayerCity.EgyptianCities.FirstOrDefault(item => item.Key == controlSettings.PrayerCityKey);
        if (city is null)
            return;
        string cacheKey = GetPrayerCacheKey(city.Key, GetEgyptDateNow());
        if (!controlSettings.PrayerTimesCache.TryGetValue(cacheKey, out PrayerDayTimes? times))
        {
            PrayerTimesSummaryText.Text = "مواقيت اليوم لم تُحمّل بعد. اضغط «تحديث مواقيت اليوم» أو فعّل الميزة لحفظها تلقائيًا.";
            return;
        }

        PrayerTimesSummaryText.Text = string.Join("   •   ", times.ToPrayerList()
            .Select(prayer => $"{prayer.Name}: {prayer.Time.ToString("hh:mm tt", CultureInfo.InvariantCulture)}"));
    }

    private void ApplyPrayerSleepPolicy(DateTime now)
    {
        if (!controlSettings.PrayerSleepEnabled)
        {
            prayerSleepOccurrenceKey = null;
            prayerSleepUntil = null;
            prayerSleepPending = false;
            return;
        }

        PrayerSleepOccurrence? occurrence = GetActivePrayerSleepOccurrence(now);
        if (occurrence is null)
        {
            prayerSleepOccurrenceKey = null;
            prayerSleepUntil = null;
            prayerSleepPending = false;
            return;
        }

        bool isNewOccurrence = !string.Equals(prayerSleepOccurrenceKey, occurrence.Key, StringComparison.Ordinal);
        prayerSleepOccurrenceKey = occurrence.Key;
        prayerSleepUntil = occurrence.EndsAt;
        if (isNewOccurrence && !string.Equals(controlSettings.LastPrayerNotificationKey, occurrence.Key, StringComparison.Ordinal))
        {
            controlSettings.LastPrayerNotificationKey = occurrence.Key;
            SaveControlSettings();
            NotifyPrayerTime(occurrence);
        }

        if (prayerSleepRequestInProgress || prayerSleepPending || DateTime.UtcNow < nextPrayerSleepRetryUtc)
            return;
        prayerSleepPending = true;
        _ = EnterPrayerSleepAsync(occurrence.Key, isNewOccurrence ? TimeSpan.FromSeconds(4) : TimeSpan.Zero);
    }

    private async Task EnterPrayerSleepAsync(string occurrenceKey, TimeSpan notificationDelay)
    {
        try
        {
            if (notificationDelay > TimeSpan.Zero)
                await Task.Delay(notificationDelay);

            PrayerSleepOccurrence? current = GetActivePrayerSleepOccurrence(DateTime.Now);
            if (!controlSettings.PrayerSleepEnabled || current is null || current.Key != occurrenceKey)
                return;

            prayerSleepPending = false;
            prayerSleepRequestInProgress = true;
            bool enteredSleep = SetSuspendState(hibernate: false, forceCritical: true, disableWakeEvent: false);
            prayerSleepRequestInProgress = false;
            if (!enteredSleep)
            {
                nextPrayerSleepRetryUtc = DateTime.UtcNow.AddSeconds(10);
                int errorCode = Marshal.GetLastWin32Error();
                PrayerTimesStatusText.Text = $"تعذر إدخال الجهاز في السكون (خطأ Windows {errorCode}). سأعيد المحاولة أثناء مدة الصلاة.";
            }
            else
            {
                nextPrayerSleepRetryUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            prayerSleepRequestInProgress = false;
            prayerSleepPending = false;
            nextPrayerSleepRetryUtc = DateTime.UtcNow.AddSeconds(10);
            PrayerTimesStatusText.Text = $"تعذر إدخال الجهاز في السكون: {ex.Message}";
        }
        finally
        {
            prayerSleepPending = false;
        }
    }

    private void NotifyPrayerTime(PrayerSleepOccurrence occurrence)
    {
        PrayerCity? city = PrayerCity.EgyptianCities.FirstOrDefault(item => item.Key == controlSettings.PrayerCityKey);
        string cityName = city?.Name ?? "مدينتك";
        string message = $"حان وقت صلاة {occurrence.PrayerName} في {cityName}. يلا نصلي ونرجع براحة؛ الجهاز هيدخل السكون {controlSettings.PrayerSleepMinutes} دقيقة.";
        PrayerTimesStatusText.Text = message;
        ScheduleNotificationText.Text = message;
        ScheduleNotificationBanner.Visibility = Visibility.Visible;
        trayIcon.ShowBalloonTip(8000, "حان وقت الصلاة", message, System.Windows.Forms.ToolTipIcon.Info);
    }

    private PrayerSleepOccurrence? GetActivePrayerSleepOccurrence(DateTime localNow)
    {
        if (!controlSettings.PrayerSleepEnabled || controlSettings.EnabledPrayers.Count == 0)
            return null;
        PrayerCity? city = PrayerCity.EgyptianCities.FirstOrDefault(item => item.Key == controlSettings.PrayerCityKey);
        if (city is null)
            return null;
        DateOnly egyptDate = GetEgyptDateNow();
        if (!controlSettings.PrayerTimesCache.TryGetValue(GetPrayerCacheKey(city.Key, egyptDate), out PrayerDayTimes? times))
            return null;

        return times.ToPrayerList()
            .Where(prayer => controlSettings.EnabledPrayers.Contains(prayer.Key, StringComparer.OrdinalIgnoreCase))
            .Select(prayer =>
            {
                DateTime startsAt = ConvertEgyptPrayerTimeToLocal(egyptDate, prayer.Time);
                return new PrayerSleepOccurrence(
                    $"{city.Key}|{egyptDate:yyyyMMdd}|{prayer.Key}",
                    prayer.Name,
                    startsAt,
                    startsAt.AddMinutes(controlSettings.PrayerSleepMinutes));
            })
            .Where(item => localNow >= item.StartsAt && localNow < item.EndsAt)
            .OrderByDescending(item => item.StartsAt)
            .FirstOrDefault();
    }

    private static DateOnly GetEgyptDateNow()
    {
        TimeZoneInfo egyptTimeZone = GetEgyptTimeZone();
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, egyptTimeZone));
    }

    private static DateTime ConvertEgyptPrayerTimeToLocal(DateOnly date, TimeOnly time)
    {
        DateTime egyptLocalTime = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(egyptLocalTime, GetEgyptTimeZone());
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
    }

    private static TimeZoneInfo GetEgyptTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    private static string GetPrayerCacheKey(string cityKey, DateOnly date) =>
        $"{cityKey}|{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    private static void SetTimePicker(ComboBox hourPicker, ComboBox minutePicker,
        ComboBox periodPicker, TimeOnly time)
    {
        int hour12 = time.Hour % 12;
        if (hour12 == 0)
            hour12 = 12;
        hourPicker.SelectedItem = hour12.ToString("00", CultureInfo.InvariantCulture);
        minutePicker.SelectedItem = time.Minute.ToString("00", CultureInfo.InvariantCulture);
        periodPicker.SelectedItem = time.Hour < 12 ? "AM" : "PM";
    }

    private static bool TryReadTimePicker(ComboBox hourPicker, ComboBox minutePicker,
        ComboBox periodPicker, out TimeOnly time)
    {
        time = default;
        if (!int.TryParse(hourPicker.SelectedItem?.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int hour12) || hour12 is < 1 or > 12 ||
            !int.TryParse(minutePicker.SelectedItem?.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out int minute) || minute is < 0 or > 59)
            return false;

        string? period = periodPicker.SelectedItem?.ToString();
        if (period is not ("AM" or "PM"))
            return false;

        int hour24 = (hour12 % 12) + (period == "PM" ? 12 : 0);
        time = new TimeOnly(hour24, minute);
        return true;
    }

    // تحديث قائمة أنشطة اليوم المختار
    private void RefreshScheduleList()
    {
        if (ScheduleListBox == null)
            return;

        isRefreshing = true;

        string selectedDay = GetSelectedDayTag();

        var orderedEntries = scheduleEntries
            .Where(entry => entry.DayTag == selectedDay)
            .OrderBy(GetActivityOrder)
            .ToList();

        ScheduleListBox.ItemsSource = null;
        ScheduleListBox.ItemsSource = orderedEntries;

        EmptyScheduleText.Visibility =
            orderedEntries.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (orderedEntries.Count > 0)
        {
            ScheduleListBox.SelectedIndex = 0;
        }
        else
        {
            ScheduleListBox.SelectedItem = null;
            ClearActivityFields();
        }

        isRefreshing = false;

        if (ScheduleListBox.SelectedItem is ScheduleEntry selectedEntry)
        {
            ShowActivityFields(selectedEntry);
        }
    }

    private void ScheduleListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (isRefreshing)
            return;

        if (ScheduleListBox.SelectedItem is ScheduleEntry entry)
        {
            ShowActivityFields(entry);
        }
        else
        {
            ClearActivityFields();
        }
    }

    // تعبئة الحقول وعرض الوقت بنظام 12 ساعة
    private void ShowActivityFields(ScheduleEntry entry)
    {
        ActivityNameTextBox.Text = entry.ActivityName;

        SetTimePicker(StartHourComboBox, StartMinuteComboBox, StartPeriodComboBox, entry.StartTime);
        SetTimePicker(EndHourComboBox, EndMinuteComboBox, EndPeriodComboBox, entry.EndTime);
        BlockedApplicationsListBox.ItemsSource = entry.BlockedApplications.ToList();
        BlockedWebsitesListBox.ItemsSource = entry.BlockedWebsites.ToList();
        AllowedApplicationsDuringActivityListBox.ItemsSource = entry.AllowedApplicationsDuringActivity.ToList();
        AllowedWebsitesDuringActivityListBox.ItemsSource = entry.AllowedWebsitesDuringActivity.ToList();
        BlockAllApplicationsExceptCheckBox.IsChecked = entry.BlockAllApplicationsExcept;
        BlockAllWebsitesExceptCheckBox.IsChecked = entry.BlockAllWebsitesExcept;
        BlockedApplicationsListBox.IsEnabled = !entry.BlockAllApplicationsExcept;
        AddBlockedApplicationButton.IsEnabled = !entry.BlockAllApplicationsExcept;
        RemoveBlockedApplicationButton.IsEnabled = !entry.BlockAllApplicationsExcept;
        BlockedWebsitesListBox.IsEnabled = !entry.BlockAllWebsitesExcept;
        BlockedWebsiteTextBox.IsEnabled = !entry.BlockAllWebsitesExcept;
        AddBlockedWebsiteButton.IsEnabled = !entry.BlockAllWebsitesExcept;
        RemoveBlockedWebsiteButton.IsEnabled = !entry.BlockAllWebsitesExcept;
        AllowedApplicationsDuringActivityListBox.IsEnabled = true;
        AllowedWebsitesDuringActivityListBox.IsEnabled = true;
        AllowedWebsiteDuringActivityTextBox.IsEnabled = true;
        BlockApplicationsForActivityCheckBox.IsChecked = entry.BlockApplications;
        BlockWebsitesForActivityCheckBox.IsChecked = entry.BlockWebsites;
        BlockApplicationsForActivityCheckBox.Visibility = entry.BlockedApplications.Count > 0 && !entry.BlockAllApplicationsExcept
            ? Visibility.Visible : Visibility.Collapsed;
        BlockWebsitesForActivityCheckBox.Visibility = entry.BlockedWebsites.Count > 0 && !entry.BlockAllWebsitesExcept
            ? Visibility.Visible : Visibility.Collapsed;
        SleepSettingsPanel.Visibility = IsSleepActivity(entry) ? Visibility.Visible : Visibility.Collapsed;
        SleepGraceMinutesComboBox.SelectedValue = controlSettings.SleepGraceMinutes;
        BlockedWebsiteTextBox.Text = "";
    }

    private void ClearActivityFields()
    {
        if (ActivityNameTextBox == null)
            return;

        ActivityNameTextBox.Text = "";
        StartHourComboBox.SelectedItem = null;
        StartMinuteComboBox.SelectedItem = null;
        StartPeriodComboBox.SelectedItem = null;
        EndHourComboBox.SelectedItem = null;
        EndMinuteComboBox.SelectedItem = null;
        EndPeriodComboBox.SelectedItem = null;
        BlockedApplicationsListBox.ItemsSource = null;
        BlockedWebsitesListBox.ItemsSource = null;
        AllowedApplicationsDuringActivityListBox.ItemsSource = null;
        AllowedWebsitesDuringActivityListBox.ItemsSource = null;
        BlockAllApplicationsExceptCheckBox.IsChecked = false;
        BlockAllWebsitesExceptCheckBox.IsChecked = false;
        BlockedApplicationsListBox.IsEnabled = true;
        AddBlockedApplicationButton.IsEnabled = true;
        RemoveBlockedApplicationButton.IsEnabled = true;
        BlockedWebsitesListBox.IsEnabled = true;
        BlockedWebsiteTextBox.IsEnabled = true;
        AddBlockedWebsiteButton.IsEnabled = true;
        RemoveBlockedWebsiteButton.IsEnabled = true;
        AllowedApplicationsDuringActivityListBox.IsEnabled = true;
        AllowedWebsitesDuringActivityListBox.IsEnabled = true;
        AllowedWebsiteDuringActivityTextBox.IsEnabled = true;
        BlockApplicationsForActivityCheckBox.IsChecked = false;
        BlockWebsitesForActivityCheckBox.IsChecked = false;
        BlockApplicationsForActivityCheckBox.Visibility = Visibility.Collapsed;
        BlockWebsitesForActivityCheckBox.Visibility = Visibility.Collapsed;
        SleepSettingsPanel.Visibility = Visibility.Collapsed;
        BlockedWebsiteTextBox.Text = "";
    }

    private ScheduleEntry? GetSelectedScheduleEntry()
    {
        return ScheduleListBox.SelectedItem as ScheduleEntry;
    }

    private List<ScheduleEntry> GetEntriesInBlockingScope(ScheduleEntry selectedEntry)
    {
        string? scope = (BlockScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.Equals(scope, "week", StringComparison.Ordinal))
            return new List<ScheduleEntry> { selectedEntry };

        return scheduleEntries
            .Where(entry => entry.StartTime == selectedEntry.StartTime && entry.EndTime == selectedEntry.EndTime)
            .ToList();
    }

    private bool ConfirmBlockingScopeChange(string changeDescription, ScheduleEntry selectedEntry,
        List<ScheduleEntry> targets)
    {
        bool isWeekScope = (BlockScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "week";
        string scopeText = isWeekScope
            ? $"الفترة المطابقة طوال الأسبوع (الأيام: {string.Join("، ", targets.Select(target => target.DayName).Distinct())})"
            : $"يوم {selectedEntry.DayName} فقط";
        return MessageBox.Show(
            $"{changeDescription}\nسيُطبّق هذا التغيير على {scopeText}.\nهل تريد المتابعة؟",
            "تأكيد نطاق التغيير",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private List<ScheduleEntry> GetEntriesInActivityEditScope(ScheduleEntry selectedEntry, bool applyToWeek)
    {
        if (!applyToWeek)
            return new List<ScheduleEntry> { selectedEntry };

        // Match the same named activity and end time, allowing a previously
        // changed start time on one day to be synchronized with the rest.
        return scheduleEntries
            .Where(entry =>
                string.Equals(entry.ActivityName.Trim(), selectedEntry.ActivityName.Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                entry.EndTime == selectedEntry.EndTime)
            .ToList();
    }

    private void ActivityBlockOption_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? selectedEntry = GetSelectedScheduleEntry();
        if (selectedEntry is null)
            return;

        List<ScheduleEntry> targets = GetEntriesInBlockingScope(selectedEntry);
        var previousValues = targets.ToDictionary(entry => entry, entry =>
            (entry.BlockApplications, entry.BlockWebsites));
        bool previousApplicationsMaster = controlSettings.ApplicationControlEnabled;
        bool previousWebsitesMaster = controlSettings.WebsiteControlEnabled;
        bool blockApplications = BlockApplicationsForActivityCheckBox.IsChecked == true;
        bool blockWebsites = BlockWebsitesForActivityCheckBox.IsChecked == true;
        if ((blockApplications && selectedEntry.BlockAllApplicationsExcept) ||
            (blockWebsites && selectedEntry.BlockAllWebsitesExcept))
        {
            MessageBox.Show("أوقف خيار «حظر الكل ما عدا» لهذه الفئة أولًا قبل تفعيل الحجب المحدد.",
                "اختر طريقة حجب واحدة", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowActivityFields(selectedEntry);
            return;
        }
        bool applicationOptionChanged = targets.Any(entry => entry.BlockApplications != blockApplications);
        bool websiteOptionChanged = targets.Any(entry => entry.BlockWebsites != blockWebsites);
        if (!applicationOptionChanged && !websiteOptionChanged)
            return;

        string scopeDescription = (BlockScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "week"
            ? $"الفترة المطابقة طوال الأسبوع ({string.Join("، ", targets.Select(entry => entry.DayName).Distinct())})"
            : $"يوم {selectedEntry.DayName} فقط";
        var confirmationLines = new List<string>();
        if (applicationOptionChanged)
            confirmationLines.Add($"حجب التطبيقات: {(blockApplications ? "مفعّل" : "متوقف")}");
        if (websiteOptionChanged)
            confirmationLines.Add($"حجب المواقع: {(blockWebsites ? "مفعّل" : "متوقف")}");
        if (blockApplications && applicationOptionChanged)
            confirmationLines.Add("قد يُنهي StudyGuard التطبيقات التي تضيفها لقائمة الحظر، وقد تفقد العمل غير المحفوظ.");
        if (blockWebsites && websiteOptionChanged && websiteProxyRecoveryFailed)
        {
            MessageBox.Show(
                "تعذرت استعادة إعداد الإنترنت السابق؛ لا يمكن تفعيل حجب المواقع قبل إصلاح الاستعادة.",
                "استعادة إعداد الإنترنت مطلوبة",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ShowActivityFields(selectedEntry);
            return;
        }

        if (blockWebsites && websiteOptionChanged)
            confirmationLines.Add("سيستخدم StudyGuard وكيل Windows لحجب النطاقات في القائمة؛ قد يتجاوز الحجب متصفح يستخدم وكيلًا خاصًا.");

        MessageBoxResult confirmation = MessageBox.Show(
            $"سيُحفظ التغيير التالي على {scopeDescription}:\n{string.Join("\n", confirmationLines)}\nهل تريد المتابعة؟",
            "تأكيد حفظ خيارات الحجب",
            MessageBoxButton.YesNo,
            blockApplications && applicationOptionChanged ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            ShowActivityFields(selectedEntry);
            return;
        }

        foreach (ScheduleEntry entry in targets)
        {
            entry.BlockApplications = blockApplications;
            entry.BlockWebsites = blockWebsites;
        }
        controlSettings.ApplicationControlEnabled = scheduleEntries.Any(entry => entry.BlockApplications);
        controlSettings.WebsiteControlEnabled = !websiteProxyRecoveryFailed &&
            scheduleEntries.Any(entry => entry.BlockWebsites);

        if (!SaveSchedule() || !SaveControlSettings())
        {
            foreach (var previous in previousValues)
            {
                previous.Key.BlockApplications = previous.Value.Item1;
                previous.Key.BlockWebsites = previous.Value.Item2;
            }
            controlSettings.ApplicationControlEnabled = previousApplicationsMaster;
            controlSettings.WebsiteControlEnabled = previousWebsitesMaster;
            SaveSchedule();
            SaveControlSettings();
            ShowActivityFields(selectedEntry);
            return;
        }

        ApplyApplicationPolicy();
        RefreshHomeSummary();
        ScheduleStatusText.Text = $"تم حفظ خيارات الحجب على {scopeDescription}.";
    }
    private void RefreshPeriodBlockLists(ScheduleEntry entry)
    {
        BlockedApplicationsListBox.ItemsSource = null;
        BlockedApplicationsListBox.ItemsSource = entry.BlockedApplications.ToList();
        BlockedWebsitesListBox.ItemsSource = null;
        BlockedWebsitesListBox.ItemsSource = entry.BlockedWebsites.ToList();
        AllowedApplicationsDuringActivityListBox.ItemsSource = entry.AllowedApplicationsDuringActivity.ToList();
        AllowedWebsitesDuringActivityListBox.ItemsSource = entry.AllowedWebsitesDuringActivity.ToList();
        BlockAllApplicationsExceptCheckBox.IsChecked = entry.BlockAllApplicationsExcept;
        BlockAllWebsitesExceptCheckBox.IsChecked = entry.BlockAllWebsitesExcept;
        BlockedApplicationsListBox.IsEnabled = !entry.BlockAllApplicationsExcept;
        AddBlockedApplicationButton.IsEnabled = !entry.BlockAllApplicationsExcept;
        RemoveBlockedApplicationButton.IsEnabled = !entry.BlockAllApplicationsExcept;
        BlockedWebsitesListBox.IsEnabled = !entry.BlockAllWebsitesExcept;
        BlockedWebsiteTextBox.IsEnabled = !entry.BlockAllWebsitesExcept;
        AddBlockedWebsiteButton.IsEnabled = !entry.BlockAllWebsitesExcept;
        RemoveBlockedWebsiteButton.IsEnabled = !entry.BlockAllWebsitesExcept;
        AllowedApplicationsDuringActivityListBox.IsEnabled = true;
        AllowedWebsitesDuringActivityListBox.IsEnabled = true;
        AllowedWebsiteDuringActivityTextBox.IsEnabled = true;
        BlockApplicationsForActivityCheckBox.IsChecked = entry.BlockApplications;
        BlockApplicationsForActivityCheckBox.Visibility = entry.BlockedApplications.Count > 0 && !entry.BlockAllApplicationsExcept
            ? Visibility.Visible : Visibility.Collapsed;
        BlockWebsitesForActivityCheckBox.IsChecked = entry.BlockWebsites;
        BlockWebsitesForActivityCheckBox.Visibility = entry.BlockedWebsites.Count > 0 && !entry.BlockAllWebsitesExcept
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshExceptionLists()
    {
        ExceptionApplicationsListBox.ItemsSource = null;
        ExceptionApplicationsListBox.ItemsSource = controlSettings.ExceptionApplications.ToList();
        ExceptionWebsitesListBox.ItemsSource = null;
        ExceptionWebsitesListBox.ItemsSource = controlSettings.ExceptionWebsites.ToList();
    }

    private void RefreshPermanentWebsiteBlocks()
    {
        PermanentWebsiteBlocksListBox.ItemsSource = null;
        PermanentWebsiteBlocksListBox.ItemsSource = controlSettings.PermanentWebsiteBlocks
            .OrderBy(block => block.AddedAtUtc)
            .ToList();
        UpdatePermanentWebsiteRemovalState();
        PermanentWebsiteStatusText.Text = websiteProxyRecoveryFailed
            ? "إعداد الإنترنت يحتاج إلى استعادة؛ تعذر تطبيق الحجب الدائم حاليًا."
            : controlSettings.PermanentWebsiteBlocks.Count == 0
                ? "أضف موقعًا ليُحجب طوال الوقت."
                : "الحجب الدائم يعمل بدون الاعتماد على جدول الأنشطة.";
    }

    private void PermanentWebsiteBlocksListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePermanentWebsiteRemovalState();
    }

    private void UpdatePermanentWebsiteRemovalState()
    {
        if (RemovePermanentWebsiteButton is null)
            return;

        if (PermanentWebsiteBlocksListBox.SelectedItem is not PermanentWebsiteBlock selected)
        {
            RemovePermanentWebsiteButton.IsEnabled = false;
            RemovePermanentWebsiteButton.Content = "اختار موقعًا من القائمة";
            return;
        }

        TimeSpan remaining = selected.RemovalAvailableAtUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            RemovePermanentWebsiteButton.IsEnabled = true;
            RemovePermanentWebsiteButton.Content = "إزالة الموقع المحدد بعد اكتمال 15 يومًا";
            return;
        }

        RemovePermanentWebsiteButton.IsEnabled = false;
        int days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
        RemovePermanentWebsiteButton.Content = $"لا يمكن الحذف الآن — متاح بعد {days} يومًا";
    }

    private void AddPermanentWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureUserStartupRegistration();
        if (startupRegistrationError is not null)
        {
            PermanentWebsiteStatusText.Text = "تعذر ضمان استمرار الحجب بعد إعادة تشغيل Windows؛ أصلح بدء التشغيل أولًا من الإعدادات.";
            return;
        }

        if (!TryNormalizeWebsiteHost(PermanentWebsiteTextBox.Text, out string host))
        {
            PermanentWebsiteStatusText.Text = "اكتب عنوان موقع صحيحًا، مثل youtube.com.";
            return;
        }

        if (controlSettings.PermanentWebsiteBlocks.Any(block =>
                string.Equals(block.Domain, host, StringComparison.OrdinalIgnoreCase)))
        {
            PermanentWebsiteStatusText.Text = "الموقع موجود بالفعل في قائمة الحجب الدائم.";
            return;
        }

        if (websiteProxyRecoveryFailed)
        {
            PermanentWebsiteStatusText.Text = "استعد إعداد الإنترنت أولًا، ثم أضف الموقع ليُطبّق الحجب الدائم.";
            return;
        }

        if (MessageBox.Show(
                $"سيُحجب {host} طوال الوقت، حتى لو كان ضمن الاستثناءات أو خارج الفترات المجدولة. لن تتمكن من إزالته قبل مرور 15 يومًا. هل تريد إضافته؟",
                "تأكيد الحجب الدائم",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var block = new PermanentWebsiteBlock { Domain = host, AddedAtUtc = DateTime.UtcNow };
        controlSettings.PermanentWebsiteBlocks.Add(block);
        if (!SaveControlSettings())
        {
            controlSettings.PermanentWebsiteBlocks.Remove(block);
            return;
        }

        PermanentWebsiteTextBox.Clear();
        RefreshPermanentWebsiteBlocks();
        ApplyApplicationPolicy();
        PermanentWebsiteStatusText.Text = $"أُضيف {host} للحجب الدائم. يمكن إزالته بعد 15 يومًا.";
    }

    private void RemovePermanentWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PermanentWebsiteBlocksListBox.SelectedItem is not PermanentWebsiteBlock block)
        {
            PermanentWebsiteStatusText.Text = "اختار موقعًا من القائمة أولًا.";
            return;
        }

        if (DateTime.UtcNow < block.RemovalAvailableAtUtc)
        {
            UpdatePermanentWebsiteRemovalState();
            return;
        }

        if (MessageBox.Show(
                $"مرّ 15 يومًا على إضافة {block.Domain}. هل تريد إزالته من الحجب الدائم؟",
                "تأكيد إزالة الموقع",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int index = controlSettings.PermanentWebsiteBlocks.IndexOf(block);
        controlSettings.PermanentWebsiteBlocks.Remove(block);
        if (!SaveControlSettings())
        {
            controlSettings.PermanentWebsiteBlocks.Insert(Math.Clamp(index, 0, controlSettings.PermanentWebsiteBlocks.Count), block);
            return;
        }

        ApplyApplicationPolicy();
        RefreshPermanentWebsiteBlocks();
        PermanentWebsiteStatusText.Text = $"أُزيل {block.Domain} من الحجب الدائم.";
    }

    private static bool TryNormalizeWebsiteHost(string? input, out string host)
    {
        host = "";
        string value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return false;

        string address = value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return false;

        host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return host.Length > 0 && !host.Contains('*');
    }

    private void AddExceptionApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختار تطبيقًا مستثنى من الحظر",
            Filter = "ملفات التطبيقات (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string path = Path.GetFullPath(dialog.FileName);
        if (controlSettings.ExceptionApplications.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            ExceptionsStatusText.Text = "التطبيق موجود بالفعل ضمن الاستثناءات.";
            return;
        }

        if (MessageBox.Show(
                "سيُسمح لهذا التطبيق في جميع الأيام والأنشطة، حتى إن كان ضمن قائمة حظر. هل تريد إضافته للاستثناءات؟",
                "تأكيد استثناء التطبيق",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        controlSettings.ExceptionApplications.Add(path);
        if (!SaveControlSettings())
        {
            controlSettings.ExceptionApplications.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            return;
        }
        RefreshExceptionLists();
        ApplyApplicationPolicy();
        ExceptionsStatusText.Text = "تمت إضافة التطبيق؛ سيظل مسموحًا في جميع الأوضاع.";
    }

    private void RemoveExceptionApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExceptionApplicationsListBox.SelectedItem is not string path)
        {
            ExceptionsStatusText.Text = "اختار تطبيقًا من القائمة أولًا.";
            return;
        }

        if (MessageBox.Show(
                "سيُزال هذا التطبيق من الاستثناءات العامة لكل الأيام. إذا كان موجودًا في قائمة حظر نشاط، فسيصبح قابلًا للحظر أثناءه. هل تريد المتابعة؟",
                "تأكيد إزالة الاستثناء",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        controlSettings.ExceptionApplications.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (!SaveControlSettings())
        {
            controlSettings.ExceptionApplications.Add(path);
            return;
        }
        RefreshExceptionLists();
        ApplyApplicationPolicy();
        ExceptionsStatusText.Text = "تمت إزالة التطبيق من الاستثناءات.";
    }

    private void AddExceptionWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        string input = ExceptionWebsiteTextBox.Text.Trim();
        string address = input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            MessageBox.Show("اكتب اسم موقع صحيحًا مثل example.com.", "عنوان الموقع غير صحيح",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (controlSettings.ExceptionWebsites.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            ExceptionsStatusText.Text = "الموقع موجود بالفعل ضمن الاستثناءات.";
            return;
        }

        if (MessageBox.Show(
                $"سيُسمح للموقع {host} في جميع الأيام والأنشطة، حتى إن كان ضمن قائمة حظر. هل تريد إضافته للاستثناءات؟",
                "تأكيد استثناء الموقع",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        controlSettings.ExceptionWebsites.Add(host);
        if (!SaveControlSettings())
        {
            controlSettings.ExceptionWebsites.RemoveAll(item => string.Equals(item, host, StringComparison.OrdinalIgnoreCase));
            return;
        }
        ExceptionWebsiteTextBox.Clear();
        RefreshExceptionLists();
        ApplyApplicationPolicy();
        ExceptionsStatusText.Text = "تمت إضافة الموقع؛ سيظل مسموحًا في جميع الأوضاع.";
    }

    private void RemoveExceptionWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExceptionWebsitesListBox.SelectedItem is not string host)
        {
            ExceptionsStatusText.Text = "اختار موقعًا من القائمة أولًا.";
            return;
        }

        if (MessageBox.Show(
                $"سيُزال الموقع {host} من الاستثناءات العامة لكل الأيام. إذا كان موجودًا في قائمة حظر نشاط، فسيصبح قابلًا للحظر أثناءه. هل تريد المتابعة؟",
                "تأكيد إزالة الاستثناء",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        controlSettings.ExceptionWebsites.RemoveAll(item => string.Equals(item, host, StringComparison.OrdinalIgnoreCase));
        if (!SaveControlSettings())
        {
            controlSettings.ExceptionWebsites.Add(host);
            return;
        }
        RefreshExceptionLists();
        ApplyApplicationPolicy();
        ExceptionsStatusText.Text = "تمت إزالة الموقع من الاستثناءات.";
    }

    private void AddBlockedApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null)
        {
            ShowNoSelectedActivityMessage();
            return;
        }
        if (entry.BlockAllApplicationsExcept)
        {
            ScheduleStatusText.Text = "حجب التطبيقات المحددة متوقف؛ أوقف «حظر كل التطبيقات ما عدا» أولًا.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "اختار التطبيق الذي تريد حظره أثناء هذا النشاط",
            Filter = "ملفات التطبيقات (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string applicationPath = Path.GetFullPath(dialog.FileName);
        if (controlSettings.ExceptionApplications.Contains(applicationPath, StringComparer.OrdinalIgnoreCase))
        {
            ScheduleStatusText.Text = "هذا التطبيق مستثنى دائمًا؛ أزله من الاستثناءات أولًا إذا أردت حظره.";
            return;
        }
        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (targets.All(target => target.BlockedApplications.Contains(applicationPath, StringComparer.OrdinalIgnoreCase)))
        {
            ScheduleStatusText.Text = "التطبيق موجود بالفعل في قائمة الحظر لهذا النشاط.";
            return;
        }
        if (!ConfirmBlockingScopeChange(
                "سيُضاف التطبيق إلى قائمة الحظر، وسيُفعّل حظره تلقائيًا. قد يُغلق التطبيق فور بدء الفترة وقد تفقد العمل غير المحفوظ.",
                entry, targets))
            return;

        bool previousApplicationMaster = controlSettings.ApplicationControlEnabled;
        var previousValues = targets.ToDictionary(target => target, target =>
            (target.AllowedApplications.ToList(), target.BlockedApplications.ToList(), target.BlockApplications));
        foreach (ScheduleEntry target in targets)
        {
            target.AllowedApplications.RemoveAll(path =>
                string.Equals(path, applicationPath, StringComparison.OrdinalIgnoreCase));
            if (!target.BlockedApplications.Contains(applicationPath, StringComparer.OrdinalIgnoreCase))
                target.BlockedApplications.Add(applicationPath);
            target.BlockApplications = target.BlockedApplications.Count > 0;
        }
        controlSettings.ApplicationControlEnabled = scheduleEntries.Any(target => target.BlockApplications);
        if (!SaveSchedule() || !SaveControlSettings())
        {
            foreach (var previous in previousValues)
            {
                previous.Key.AllowedApplications = previous.Value.Item1;
                previous.Key.BlockedApplications = previous.Value.Item2;
                previous.Key.BlockApplications = previous.Value.Item3;
            }
            controlSettings.ApplicationControlEnabled = previousApplicationMaster;
            SaveSchedule();
            SaveControlSettings();
            return;
        }

        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = targets.Count > 1
            ? "أُضيف التطبيق وتفعّل حظره تلقائيًا في الفترة نفسها طوال الأسبوع."
            : "أُضيف التطبيق وتفعّل حظره تلقائيًا في هذا اليوم.";
    }

    private void RemoveBlockedApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null || BlockedApplicationsListBox.SelectedItem is not string applicationPath)
        {
            ShowNoSelectedActivityMessage();
            return;
        }
        if (entry.BlockAllApplicationsExcept)
        {
            ScheduleStatusText.Text = "حجب التطبيقات المحددة متوقف أثناء استخدام قائمة السماح الشاملة.";
            return;
        }

        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (!ConfirmBlockingScopeChange("سيُزال التطبيق من قائمة الحظر.", entry, targets))
            return;
        bool previousApplicationMaster = controlSettings.ApplicationControlEnabled;
        var previousValues = targets.ToDictionary(target => target, target =>
            (target.BlockedApplications.ToList(), target.BlockApplications));
        foreach (ScheduleEntry target in targets)
        {
            target.BlockedApplications.RemoveAll(path =>
                string.Equals(path, applicationPath, StringComparison.OrdinalIgnoreCase));
            if (target.BlockedApplications.Count == 0)
                target.BlockApplications = false;
        }
        controlSettings.ApplicationControlEnabled = scheduleEntries.Any(target => target.BlockApplications);
        if (!SaveSchedule() || !SaveControlSettings())
        {
            foreach (var previous in previousValues)
            {
                previous.Key.BlockedApplications = previous.Value.Item1;
                previous.Key.BlockApplications = previous.Value.Item2;
            }
            controlSettings.ApplicationControlEnabled = previousApplicationMaster;
            SaveSchedule();
            SaveControlSettings();
            return;
        }

        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = targets.Count > 1
            ? "أُزيل التطبيق من حظر الفترة نفسها في كل أيام الأسبوع."
            : "أُزيل التطبيق من حظر هذا اليوم.";
    }

    private void AddBlockedWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null)
        {
            ShowNoSelectedActivityMessage();
            return;
        }
        if (entry.BlockAllWebsitesExcept)
        {
            ScheduleStatusText.Text = "حجب المواقع المحددة متوقف؛ أوقف «حظر كل المواقع ما عدا» أولًا.";
            return;
        }

        string input = BlockedWebsiteTextBox.Text.Trim();
        string address = input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            MessageBox.Show("اكتب اسم نطاق صحيحًا مثل example.com.", "عنوان الموقع غير صحيح",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (controlSettings.ExceptionWebsites.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            ScheduleStatusText.Text = "هذا النطاق مستثنى دائمًا؛ أزله من الاستثناءات أولًا إذا أردت حظره.";
            return;
        }

        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (targets.All(target => target.BlockedWebsites.Contains(host, StringComparer.OrdinalIgnoreCase)))
        {
            ScheduleStatusText.Text = "الموقع موجود بالفعل في قائمة الحظر لهذه الفترة.";
            return;
        }
        if (websiteProxyRecoveryFailed)
        {
            MessageBox.Show(
                "لا يمكن تفعيل حجب المواقع قبل استعادة إعداد الإنترنت السابق. أعد تشغيل StudyGuard وحاول مرة أخرى.",
                "حجب المواقع غير متاح",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (!ConfirmBlockingScopeChange(
                $"سيُضاف الموقع {host} إلى قائمة الحظر وسيبدأ حظره تلقائيًا أثناء الفترة. قد يتجاوز الحجب متصفحًا يستخدم وكيلًا خاصًا.",
                entry, targets))
            return;

        bool previousWebsiteMaster = controlSettings.WebsiteControlEnabled;
        var previousValues = targets.ToDictionary(target => target, target =>
            (target.BlockedWebsites.ToList(), target.BlockWebsites));
        foreach (ScheduleEntry target in targets)
        {
            if (!target.BlockedWebsites.Contains(host, StringComparer.OrdinalIgnoreCase))
                target.BlockedWebsites.Add(host);
            target.BlockWebsites = target.BlockedWebsites.Count > 0;
        }
        controlSettings.WebsiteControlEnabled = scheduleEntries.Any(target => target.BlockWebsites);
        if (!SaveSchedule() || !SaveControlSettings())
        {
            foreach (var previous in previousValues)
            {
                previous.Key.BlockedWebsites = previous.Value.Item1;
                previous.Key.BlockWebsites = previous.Value.Item2;
            }
            controlSettings.WebsiteControlEnabled = previousWebsiteMaster;
            SaveSchedule();
            SaveControlSettings();
            return;
        }

        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = targets.Count > 1
            ? "أُضيف الموقع وتفعّل حظره تلقائيًا في الفترة نفسها طوال الأسبوع."
            : "أُضيف الموقع وتفعّل حظره تلقائيًا في هذا اليوم.";
    }

    private void RemoveBlockedWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null || BlockedWebsitesListBox.SelectedItem is not string host)
        {
            ShowNoSelectedActivityMessage();
            return;
        }
        if (entry.BlockAllWebsitesExcept)
        {
            ScheduleStatusText.Text = "حجب المواقع المحددة متوقف أثناء استخدام قائمة السماح الشاملة.";
            return;
        }

        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (!ConfirmBlockingScopeChange($"سيُزال الموقع {host} من قائمة الحظر.", entry, targets))
            return;
        bool previousWebsiteMaster = controlSettings.WebsiteControlEnabled;
        var previousValues = targets.ToDictionary(target => target, target =>
            (target.BlockedWebsites.ToList(), target.BlockWebsites));
        foreach (ScheduleEntry target in targets)
        {
            target.BlockedWebsites.RemoveAll(site =>
                string.Equals(site, host, StringComparison.OrdinalIgnoreCase));
            if (target.BlockedWebsites.Count == 0)
                target.BlockWebsites = false;
        }
        controlSettings.WebsiteControlEnabled = scheduleEntries.Any(target => target.BlockWebsites);
        if (!SaveSchedule() || !SaveControlSettings())
        {
            foreach (var previous in previousValues)
            {
                previous.Key.BlockedWebsites = previous.Value.Item1;
                previous.Key.BlockWebsites = previous.Value.Item2;
            }
            controlSettings.WebsiteControlEnabled = previousWebsiteMaster;
            SaveSchedule();
            SaveControlSettings();
            return;
        }

        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = targets.Count > 1
            ? "أُزيل الموقع من حظر الفترة نفسها طوال الأسبوع."
            : "أُزيل الموقع من حظر هذا اليوم.";
    }

    private void ExceptPolicyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? selectedEntry = GetSelectedScheduleEntry();
        if (selectedEntry is null)
            return;

        bool applicationsMode = sender == BlockAllApplicationsExceptCheckBox;
        bool enabled = applicationsMode
            ? BlockAllApplicationsExceptCheckBox.IsChecked == true
            : BlockAllWebsitesExceptCheckBox.IsChecked == true;
        if (enabled && !applicationsMode && websiteProxyRecoveryFailed)
        {
            MessageBox.Show("لا يمكن تفعيل حجب المواقع قبل استعادة إعداد الإنترنت السابق.",
                "استعادة إعداد الإنترنت مطلوبة", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowActivityFields(selectedEntry);
            return;
        }

        List<ScheduleEntry> targets = GetEntriesInBlockingScope(selectedEntry);
        string policy = applicationsMode ? "حظر كل التطبيقات عدا القائمة المختارة" : "حظر كل المواقع عدا النطاقات المختارة";
        if (enabled && MessageBox.Show(
                $"سيُفعّل {policy} على {targets.Count} من الفترات حسب نطاق الحجب المحدد. " +
                (applicationsMode
                    ? "قد تُغلق التطبيقات المفتوحة فورًا، باستثناء تطبيقات Windows المحمية والتطبيقات الموجودة في الاستثناءات العامة أو قائمة السماح لهذه الفترة. احفظ عملك أولًا."
                    : "سيُمنع الاتصال بكل المواقع غير الموجودة في قائمة السماح أو الاستثناءات العامة أثناء الفترة." ) +
                "\nهل تريد المتابعة؟",
                "تأكيد الحجب الشامل", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            ShowActivityFields(selectedEntry);
            return;
        }

        var previous = targets.ToDictionary(entry => entry, entry =>
            (entry.BlockApplications, entry.BlockWebsites, entry.BlockAllApplicationsExcept, entry.BlockAllWebsitesExcept));
        bool previousApplicationsMaster = controlSettings.ApplicationControlEnabled;
        bool previousWebsitesMaster = controlSettings.WebsiteControlEnabled;
        foreach (ScheduleEntry entry in targets)
        {
            if (applicationsMode)
            {
                entry.BlockAllApplicationsExcept = enabled;
                entry.BlockApplications = enabled || entry.BlockedApplications.Count > 0;
            }
            else
            {
                entry.BlockAllWebsitesExcept = enabled;
                entry.BlockWebsites = enabled || entry.BlockedWebsites.Count > 0;
            }
        }
        controlSettings.ApplicationControlEnabled = scheduleEntries.Any(entry => entry.BlockApplications);
        controlSettings.WebsiteControlEnabled = !websiteProxyRecoveryFailed && scheduleEntries.Any(entry => entry.BlockWebsites);
        if (!SaveSchedule() || !SaveControlSettings())
        {
            foreach (var item in previous)
            {
                item.Key.BlockApplications = item.Value.BlockApplications;
                item.Key.BlockWebsites = item.Value.BlockWebsites;
                item.Key.BlockAllApplicationsExcept = item.Value.BlockAllApplicationsExcept;
                item.Key.BlockAllWebsitesExcept = item.Value.BlockAllWebsitesExcept;
            }
            controlSettings.ApplicationControlEnabled = previousApplicationsMaster;
            controlSettings.WebsiteControlEnabled = previousWebsitesMaster;
            SaveSchedule();
            SaveControlSettings();
            ShowActivityFields(selectedEntry);
            return;
        }

        ShowActivityFields(selectedEntry);
        ApplyApplicationPolicy();
        RefreshHomeSummary();
        ScheduleStatusText.Text = enabled ? $"تم تفعيل {policy} للفترة المحددة." : $"تم إيقاف {policy}.";
    }

    private void AddAllowedApplicationDuringActivityButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null) { ShowNoSelectedActivityMessage(); return; }
        var dialog = new OpenFileDialog
        {
            Title = "اختار تطبيقًا مسموحًا أثناء الحجب الشامل",
            Filter = "ملفات التطبيقات (*.exe)|*.exe", CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        string path = Path.GetFullPath(dialog.FileName);
        if (controlSettings.ExceptionApplications.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            ScheduleStatusText.Text = "التطبيق موجود بالفعل ضمن المسموح.";
            return;
        }
        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (targets.All(target => target.AllowedApplicationsDuringActivity.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            ScheduleStatusText.Text = "التطبيق موجود بالفعل ضمن المسموح.";
            return;
        }
        if (!ConfirmBlockingScopeChange("سيُسمح بهذا التطبيق أثناء الحجب الشامل.", entry, targets)) return;
        var previous = targets.ToDictionary(target => target, target => target.AllowedApplicationsDuringActivity.ToList());
        foreach (ScheduleEntry target in targets)
            if (!target.AllowedApplicationsDuringActivity.Contains(path, StringComparer.OrdinalIgnoreCase))
                target.AllowedApplicationsDuringActivity.Add(path);
        if (!SaveSchedule())
        {
            foreach (var item in previous) item.Key.AllowedApplicationsDuringActivity = item.Value;
            return;
        }
        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = "تمت إضافة التطبيق إلى قائمة المسموح لهذه الفترة.";
    }

    private void RemoveAllowedApplicationDuringActivityButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null || AllowedApplicationsDuringActivityListBox.SelectedItem is not string path)
        { ShowNoSelectedActivityMessage(); return; }
        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (!ConfirmBlockingScopeChange("سيُزال التطبيق من قائمة السماح، وقد يصبح محظورًا أثناء الفترة.", entry, targets)) return;
        var previous = targets.ToDictionary(target => target, target => target.AllowedApplicationsDuringActivity.ToList());
        foreach (ScheduleEntry target in targets)
            target.AllowedApplicationsDuringActivity.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (!SaveSchedule())
        {
            foreach (var item in previous) item.Key.AllowedApplicationsDuringActivity = item.Value;
            return;
        }
        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = "تمت إزالة التطبيق من قائمة السماح.";
    }

    private void AddAllowedWebsiteDuringActivityButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null) { ShowNoSelectedActivityMessage(); return; }
        string input = AllowedWebsiteDuringActivityTextBox.Text.Trim();
        string address = input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(uri.Host))
        {
            MessageBox.Show("اكتب اسم نطاق صحيحًا مثل example.com.", "عنوان الموقع غير صحيح",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (controlSettings.ExceptionWebsites.Contains(host, StringComparer.OrdinalIgnoreCase))
        { ScheduleStatusText.Text = "النطاق موجود بالفعل ضمن المسموح."; return; }
        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (targets.All(target => target.AllowedWebsitesDuringActivity.Contains(host, StringComparer.OrdinalIgnoreCase)))
        { ScheduleStatusText.Text = "النطاق موجود بالفعل ضمن المسموح."; return; }
        if (!ConfirmBlockingScopeChange($"سيُسمح بالنطاق {host} أثناء الحجب الشامل.", entry, targets)) return;
        var previous = targets.ToDictionary(target => target, target => target.AllowedWebsitesDuringActivity.ToList());
        foreach (ScheduleEntry target in targets)
            if (!target.AllowedWebsitesDuringActivity.Contains(host, StringComparer.OrdinalIgnoreCase))
                target.AllowedWebsitesDuringActivity.Add(host);
        if (!SaveSchedule())
        {
            foreach (var item in previous) item.Key.AllowedWebsitesDuringActivity = item.Value;
            return;
        }
        AllowedWebsiteDuringActivityTextBox.Clear();
        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = $"تمت إضافة {host} إلى قائمة المواقع المسموحة لهذه الفترة.";
    }

    private void RemoveAllowedWebsiteDuringActivityButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEntry? entry = GetSelectedScheduleEntry();
        if (entry is null || AllowedWebsitesDuringActivityListBox.SelectedItem is not string host)
        { ShowNoSelectedActivityMessage(); return; }
        List<ScheduleEntry> targets = GetEntriesInBlockingScope(entry);
        if (!ConfirmBlockingScopeChange($"سيُزال النطاق {host} من قائمة السماح وقد يصبح محظورًا.", entry, targets)) return;
        var previous = targets.ToDictionary(target => target, target => target.AllowedWebsitesDuringActivity.ToList());
        foreach (ScheduleEntry target in targets)
            target.AllowedWebsitesDuringActivity.RemoveAll(item => string.Equals(item, host, StringComparison.OrdinalIgnoreCase));
        if (!SaveSchedule())
        {
            foreach (var item in previous) item.Key.AllowedWebsitesDuringActivity = item.Value;
            return;
        }
        RefreshPeriodBlockLists(entry);
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = "تمت إزالة النطاق من قائمة المواقع المسموحة.";
    }

    private static void ShowNoSelectedActivityMessage()
    {
        MessageBox.Show(
            "اختار نشاطًا من القائمة أولًا.",
            "StudyGuard",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // حفظ تعديلات النشاط المحدد
    private void SaveActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleListBox.SelectedItem is not ScheduleEntry entry)
        {
            MessageBox.Show(
                "اختار نشاطًا من القائمة أولًا.",
                "StudyGuard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        string name = ActivityNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(
                "اكتب اسم النشاط.",
                "اسم النشاط مطلوب",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        if (!TryReadTimePicker(StartHourComboBox, StartMinuteComboBox, StartPeriodComboBox, out TimeOnly startTime))
        {
            MessageBox.Show(
                "اختار الساعة والدقيقة والفترة لوقت البداية.",
                "وقت البداية مطلوب",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        if (!TryReadTimePicker(EndHourComboBox, EndMinuteComboBox, EndPeriodComboBox, out TimeOnly endTime))
        {
            MessageBox.Show(
                "اختار الساعة والدقيقة والفترة لوقت النهاية.",
                "وقت النهاية مطلوب",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        if (startTime == endTime)
        {
            MessageBox.Show(
                "وقت البداية والنهاية لا يمكن أن يكونا متساويين.",
                "تحقق من الوقت",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        bool applyToWeek = (sender as System.Windows.Controls.Button)?.Tag?.ToString() == "week";
        List<ScheduleEntry> targets = GetEntriesInActivityEditScope(entry, applyToWeek);
        string targetDays = string.Join("، ", targets.Select(target => target.DayName).Distinct());
        string saveScopeText = applyToWeek
            ? $"النشاط المطابق في الأيام: {targetDays}"
            : $"يوم {entry.DayName} فقط";
        MessageBoxResult confirmSave = MessageBox.Show(
            $"سيتم حفظ «{name}» من {FormatTime(startTime)} إلى {FormatTime(endTime)} على {saveScopeText}.\nهل تريد المتابعة؟",
            "تأكيد حفظ النشاط",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmSave != MessageBoxResult.Yes)
            return;

        var previousValues = targets.ToDictionary(target => target, target =>
            (target.ActivityName, target.StartTime, target.EndTime));
        foreach (ScheduleEntry target in targets)
        {
            target.ActivityName = name;
            target.StartTime = startTime;
            target.EndTime = endTime;
        }

        if (!SaveSchedule())
        {
            foreach (var previous in previousValues)
            {
                previous.Key.ActivityName = previous.Value.ActivityName;
                previous.Key.StartTime = previous.Value.StartTime;
                previous.Key.EndTime = previous.Value.EndTime;
            }
            return;
        }

        RefreshScheduleList();
        RefreshHomeSummary();
        CheckUpcomingActivityNotifications();
        ApplyApplicationPolicy();

        // إعادة تحديد النشاط الذي تم تعديله
        ScheduleListBox.SelectedItem =
            ScheduleListBox.Items
                .Cast<ScheduleEntry>()
                .FirstOrDefault(item => ReferenceEquals(item, entry));

        ShowActivityFields(entry);

        ScheduleStatusText.Text = applyToWeek
            ? targets.Count > 1
                ? $"تم تحديث النشاط في {targets.Count} أيام لهذا الأسبوع."
                : "لم يوجد نشاط مطابق في الأيام الأخرى؛ تم تحديث اليوم المحدد فقط."
            : "تم حفظ تعديلات النشاط لهذا اليوم فقط.";
    }

    // إضافة نشاط جديد لليوم المختار
    private void AddActivityButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddActivityDialog(GetSelectedDayTag())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        string dayNames = string.Join("، ", dialog.SelectedDays.Select(day => day.Name));
        MessageBoxResult confirmAdd = MessageBox.Show(
            $"سيُضاف نشاط «{dialog.ActivityName}» من {FormatTime(dialog.StartTime)} إلى {FormatTime(dialog.EndTime)} في الأيام التالية: {dayNames}.\nهل تريد المتابعة؟",
            "تأكيد إضافة النشاط",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmAdd != MessageBoxResult.Yes)
            return;

        var newEntries = dialog.SelectedDays
            .Select(day => new ScheduleEntry
            {
                DayTag = day.Tag,
                DayName = day.Name,
                ActivityName = dialog.ActivityName,
                StartTime = dialog.StartTime,
                EndTime = dialog.EndTime
            })
            .ToList();

        scheduleEntries.AddRange(newEntries);

        // اعرض أول نسخة أُنشئت حتى يرى المستخدم نتيجة الإضافة مباشرة.
        string firstAddedDayTag = dialog.SelectedDays[0].Tag;
        ComboBoxItem? firstAddedDay = ScheduleDayComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), firstAddedDayTag, StringComparison.Ordinal));
        if (firstAddedDay is not null)
            ScheduleDayComboBox.SelectedItem = firstAddedDay;

        if (!SaveSchedule())
        {
            foreach (ScheduleEntry entry in newEntries)
                scheduleEntries.Remove(entry);
            RefreshScheduleList();
            RefreshHomeSummary();
            return;
        }

        RefreshScheduleList();
        RefreshHomeSummary();
        CheckUpcomingActivityNotifications();
        ApplyApplicationPolicy();

        ScheduleEntry? visibleEntry = newEntries
            .FirstOrDefault(entry => entry.DayTag == GetSelectedDayTag());
        if (visibleEntry is not null)
        {
            ScheduleListBox.SelectedItem = ScheduleListBox.Items
                .Cast<ScheduleEntry>()
                .FirstOrDefault(item => ReferenceEquals(item, visibleEntry));

            ShowActivityFields(visibleEntry);
        }

        ScheduleStatusText.Text =
            $"تمت إضافة «{dialog.ActivityName}» بنفس المواعيد إلى {dialog.SelectedDays.Count} أيام.";
    }

    // حذف النشاط المحدد
    private void DeleteScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleListBox.SelectedItem is not ScheduleEntry entry)
        {
            MessageBox.Show(
                "اختار نشاطًا من القائمة أولًا.",
                "StudyGuard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        List<ScheduleEntry> weekTargets = GetEntriesInActivityEditScope(entry, applyToWeek: true);
        string weekDays = string.Join("، ", weekTargets.Select(target => target.DayName).Distinct());
        MessageBoxResult result = MessageBox.Show(
            $"اختر نطاق الحذف للنشاط «{entry.ActivityName}»:\n\nنعم: احذفه من أيام الأسبوع التي تحتوي النشاط المطابق ({weekDays}).\nلا: احذفه من يوم {entry.DayName} فقط.\nإلغاء: لا تحذف شيئًا.",
            "تأكيد نطاق الحذف",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
            return;

        List<ScheduleEntry> targets = result == MessageBoxResult.Yes
            ? weekTargets
            : new List<ScheduleEntry> { entry };
        scheduleEntries.RemoveAll(targets.Contains);
        if (!SaveSchedule())
        {
            scheduleEntries.AddRange(targets);
            return;
        }
        RefreshScheduleList();
        RefreshHomeSummary();
        CheckUpcomingActivityNotifications();
        ApplyApplicationPolicy();

        ScheduleStatusText.Text = targets.Count > 1
            ? $"تم حذف النشاط من {targets.Count} أيام لهذا الأسبوع."
            : "تم حذف النشاط من اليوم المحدد فقط.";
    }

    private void DeleteAllScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (scheduleEntries.Count == 0)
        {
            MessageBox.Show("الجدول الأسبوعي فارغ بالفعل.", "StudyGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                $"سيُحذف نهائيًا كل الفترات والأنشطة من الأيام السبعة ({scheduleEntries.Count} فترة). لا يمكن التراجع عن هذا الإجراء. هل تريد المتابعة؟",
                "تأكيد حذف كل الفترات", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        List<ScheduleEntry> previousEntries = scheduleEntries.ToList();
        bool previousApplicationsMaster = controlSettings.ApplicationControlEnabled;
        bool previousWebsitesMaster = controlSettings.WebsiteControlEnabled;
        scheduleEntries.Clear();
        controlSettings.ApplicationControlEnabled = false;
        controlSettings.WebsiteControlEnabled = false;
        if (!SaveSchedule() || !SaveControlSettings())
        {
            scheduleEntries.AddRange(previousEntries);
            controlSettings.ApplicationControlEnabled = previousApplicationsMaster;
            controlSettings.WebsiteControlEnabled = previousWebsitesMaster;
            SaveSchedule();
            SaveControlSettings();
            return;
        }

        RefreshScheduleList();
        ClearActivityFields();
        RefreshHomeSummary();
        ApplyApplicationPolicy();
        ScheduleStatusText.Text = "تم حذف كل فترات الجدول الأسبوعي.";
    }

    // بيانات النشاط المحفوظة
    public sealed class ScheduleEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string DayName { get; set; } = "";

        public string DayTag { get; set; } = "";

        public string ActivityName { get; set; } = "";

        public TimeOnly StartTime { get; set; }

        public TimeOnly EndTime { get; set; }

        public List<string> AllowedApplications { get; set; } = new();

        public List<string> BlockedApplications { get; set; } = new();

        public List<string> AllowedApplicationsDuringActivity { get; set; } = new();

        public bool BlockAllApplicationsExcept { get; set; }

        public bool BlockApplications { get; set; }

        public bool BlockWebsites { get; set; }

        public List<string> BlockedWebsites { get; set; } = new();

        public List<string> AllowedWebsitesDuringActivity { get; set; } = new();

        public bool BlockAllWebsitesExcept { get; set; }

        // Retain compatibility with schedules created before per-period website blocking.
        public List<string> AllowedWebsites { get; set; } = new();

        public string DisplayText =>
            $"{ActivityName} | " +
            $"{StartTime.ToString("hh:mm tt", CultureInfo.InvariantCulture)} - " +
            $"{EndTime.ToString("hh:mm tt", CultureInfo.InvariantCulture)}";
    }

    public sealed class PermanentWebsiteBlock
    {
        public string Domain { get; set; } = "";
        public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public DateTime RemovalAvailableAtUtc => AddedAtUtc.ToUniversalTime().AddDays(15);

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                DateTime availableAtLocal = RemovalAvailableAtUtc.ToLocalTime();
                return DateTime.UtcNow >= RemovalAvailableAtUtc
                    ? $"{Domain}  •  اكتملت مدة 15 يومًا — متاح للإزالة"
                    : $"{Domain}  •  متاح للإزالة بعد 15 يومًا في {availableAtLocal:dd/MM/yyyy}";
            }
        }
    }

    private sealed class ControlSettings
    {
        public ControlSettings() { }

        public bool ApplicationControlEnabled { get; set; }
        public bool WebsiteControlEnabled { get; set; }
        public List<string> ExceptionApplications { get; set; } = new();
        public List<string> ExceptionWebsites { get; set; } = new();
        public List<PermanentWebsiteBlock> PermanentWebsiteBlocks { get; set; } = new();
        public bool SleepShutdownEnabled { get; set; } = true;
        public int SleepGraceMinutes { get; set; } = 3;
        public bool BlockOptionsAutoMigrationDone { get; set; }
        public string LastSleepShutdownKey { get; set; } = "";
        public bool PrayerSleepEnabled { get; set; }
        public int PrayerSleepMinutes { get; set; } = 10;
        public bool PrayerSleepDurationPreferenceApplied { get; set; }
        public string PrayerCityKey { get; set; } = "beni-suef";
        public bool PrayerCityPreferenceApplied { get; set; }
        public List<string> EnabledPrayers { get; set; } = new() { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };
        public string LastPrayerNotificationKey { get; set; } = "";
        public Dictionary<string, PrayerDayTimes> PrayerTimesCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SleepGraceOption(int Minutes, string DisplayText);

    private sealed record PrayerSleepDurationOption(int Minutes, string DisplayText);

    private sealed record ActiveScheduleActivity(ScheduleEntry Entry, DateTime StartsAt);

    private sealed record PrayerSleepOccurrence(string Key, string PrayerName, DateTime StartsAt, DateTime EndsAt);
}
