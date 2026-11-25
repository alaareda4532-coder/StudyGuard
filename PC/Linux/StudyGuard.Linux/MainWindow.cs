using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json;

namespace StudyGuard.Linux;

public sealed class MainWindow : Window
{
    private const string Bg = "#10131B", CardBg = "#151925", Accent = "#6878FF", TextColor = "#F4F6FC", Muted = "#AAB4CA";
    private readonly StudyStore store = new();
    private readonly StackPanel content = new() { Spacing = 12, Margin = new Thickness(22) };
    private readonly TextBlock pageTitle = new() { FontSize = 23, FontWeight = FontWeight.Bold, Foreground = Brush(TextColor) };
    private readonly TextBlock status = new() { Foreground = Brush(Muted), TextWrapping = TextWrapping.Wrap };
    private string page = "home";

    public MainWindow()
    {
        Title = "StudyGuard — لينكس";
        Width = 1160; Height = 780; MinWidth = 900; MinHeight = 620;
        Background = Brush(Bg); FlowDirection = FlowDirection.RightToLeft;
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220") };
        var body = new DockPanel();
        var header = new Border { Background = Brush(CardBg), Padding = new Thickness(22, 18), Child = new TextBlock { Text = "StudyGuard", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brush(TextColor), HorizontalAlignment = HorizontalAlignment.Right } };
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(scroll); Grid.SetColumn(body, 0); root.Children.Add(body);

        var nav = new StackPanel { Background = Brush(CardBg), Spacing = 8, Margin = new Thickness(0, 14, 10, 10) };
        nav.Children.Add(new TextBlock { Text = "القائمة", Foreground = Brush(Muted), Margin = new Thickness(16, 6, 16, 12) });
        AddNav(nav, "الرئيسية", "home"); AddNav(nav, "الجدول الأسبوعي", "schedule"); AddNav(nav, "الحجب الدائم", "permanent"); AddNav(nav, "الاستثناءات", "exceptions"); AddNav(nav, "التشغيل والحماية", "protection");
        Grid.SetColumn(nav, 1); root.Children.Add(nav);
        Content = root;
        ShowPage("home");
    }

    private void AddNav(Panel nav, string label, string target)
    {
        var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Right, Background = Brush(CardBg), Foreground = Brush(TextColor), Padding = new Thickness(12, 11), BorderThickness = new Thickness(0) };
        button.Click += (_, _) => ShowPage(target); nav.Children.Add(button);
    }

    private void ShowPage(string target)
    {
        page = target; content.Children.Clear(); status.Text = "";
        content.Children.Add(pageTitle);
        switch (target)
        {
            case "home": ShowHome(); break;
            case "schedule": ShowSchedule(); break;
            case "permanent": ShowPermanent(); break;
            case "exceptions": ShowExceptions(); break;
            default: ShowProtection(); break;
        }
        content.Children.Add(status);
    }

    private void ShowHome()
    {
        pageTitle.Text = "أهلًا بيك، خلّي وقتك ليك";
        var data = store.Load(); var now = DateTimeOffset.Now; var active = StudyStore.Active(data, now);
        AddCard("حالتك الآن", active is null ? "مفيش نشاط مجدول دلوقتي." : $"أنت في وضع: {active.Name}\n{active.Start} — {active.End}\n{PolicyText(active)}");
        var next = NextActivity(data, now);
        AddCard("النشاط القادم", next is null ? "أضف موعدًا من صفحة الجدول الأسبوعي." : $"{next.Value.Rule.Name} · {StudyStore.DayNames[next.Value.Day]} · {next.Value.Rule.Start}");
        AddCard("حالة الحماية", "الحجب الفعلي يحتاج تثبيت خدمة StudyGuard بصلاحية مسؤول النظام. الخدمة تعمل حتى عند إغلاق الواجهة.");
        AddCard("المطور", "علاء رضا أحمد");
    }

    private void ShowSchedule()
    {
        pageTitle.Text = "الجدول الأسبوعي";
        var data = store.Load(); var today = (int)DateTime.Now.DayOfWeek;
        var selector = new ComboBox { ItemsSource = StudyStore.DayNames, SelectedIndex = today, HorizontalAlignment = HorizontalAlignment.Stretch };
        AddCard("اختيار اليوم", "اختر اليوم لعرض أنشطته وتعديلها."); content.Children.Add(selector);
        void RefreshList()
        {
            foreach (var old in content.Children.OfType<Border>().Where(x => Equals(x.Tag, "activity" )).ToList()) content.Children.Remove(old);
            var selectedDay = selector.SelectedIndex < 0 ? today : selector.SelectedIndex;
            foreach (var rule in store.Load().Activities.Where(x => x.Days.Contains(selectedDay)).OrderBy(x => x.Start))
            {
                var card = new StackPanel { Spacing = 7 };
                card.Children.Add(Label(rule.Name, 17, true));
                card.Children.Add(Label($"{rule.Start} — {rule.End}\n{PolicyText(rule)}", 13));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
                var edit = ActionButton("تعديل", Accent); edit.Click += async (_, _) => { if (await ActivityEditor(rule)) RefreshList(); };
                var delete = ActionButton("حذف", "#472B38"); delete.Click += (_, _) => { var current = store.Load(); current.Activities.RemoveAll(x => x.Id == rule.Id); store.Save(current); RefreshList(); };
                actions.Children.Add(edit); actions.Children.Add(delete); card.Children.Add(actions);
                var border = Card(card); border.Tag = "activity"; content.Children.Add(border);
            }
        }
        selector.SelectionChanged += (_, _) => RefreshList();
        RefreshList();
        var add = ActionButton("＋ إضافة نشاط", Accent); add.Click += async (_, _) => { if (await ActivityEditor(null)) RefreshList(); };
        content.Children.Add(add);
    }

    private void ShowPermanent()
    {
        pageTitle.Text = "الحجب الدائم";
        AddCard("حظر لمدة 15 يومًا", "العناصر هنا محظورة دائمًا، ولا تستطيع الاستثناءات إلغاءها. يظهر الحذف بعد مرور 15 يومًا كاملة من الإضافة.");
        var data = store.Load();
        AddSection("التطبيقات");
        if (data.PermanentApps.Count == 0) AddCard("القائمة فارغة", "أضف اسم العملية، مثل firefox أو telegram-desktop.");
        foreach (var entry in data.PermanentApps.ToList()) AddTimedEntry(entry, false);
        var addApp = ActionButton("＋ إضافة تطبيق", Accent); addApp.Click += async (_, _) => { var result = await EntryDialog("إضافة تطبيق دائم", false); if (result is not null) ShowPage(page); };
        content.Children.Add(addApp);

        AddSection("المواقع");
        if (data.PermanentSites.Count == 0) AddCard("القائمة فارغة", "أضف نطاقًا مثل youtube.com.");
        foreach (var entry in data.PermanentSites.ToList()) AddTimedEntry(entry, true);
        var addSite = ActionButton("＋ إضافة موقع", Accent); addSite.Click += async (_, _) => { var result = await EntryDialog("إضافة موقع دائم", true); if (result is not null) ShowPage(page); };
        content.Children.Add(addSite);
        AddCard("تنبيه المواقع", "يكتب وكيل الحماية النطاقات في ملف hosts للنظام. بعض التطبيقات قد تستخدم DNS مشفرًا أو عناوين فرعية غير مدرجة.");
    }

    private void AddTimedEntry(TimedEntry entry, bool site)
    {
        var left = entry.AddedAt + StudyStore.PermanentLock - DateTimeOffset.UtcNow;
        var days = Math.Max(0, (int)Math.Ceiling(left.TotalDays));
        var layout = new StackPanel { Spacing = 6 };
        layout.Children.Add(Label(entry.Value, 15, true));
        layout.Children.Add(Label(left <= TimeSpan.Zero ? "الحذف متاح الآن." : $"الحذف متاح بعد {days} يوم.", 13));
        var remove = ActionButton(left <= TimeSpan.Zero ? "حذف" : "مقفل حتى اكتمال 15 يومًا", left <= TimeSpan.Zero ? "#472B38" : "#303646");
        remove.IsEnabled = left <= TimeSpan.Zero;
        remove.Click += (_, _) =>
        {
            var current = store.Load(); var list = site ? current.PermanentSites : current.PermanentApps;
            var target = list.FirstOrDefault(x => x.Value.Equals(entry.Value, StringComparison.OrdinalIgnoreCase));
            if (target is null || DateTimeOffset.UtcNow < target.AddedAt + StudyStore.PermanentLock) { status.Text = "مش ممكن حذف العنصر قبل مرور 15 يومًا كاملة."; return; }
            list.Remove(target); store.Save(current); ShowPage(page);
        };
        layout.Children.Add(remove); content.Children.Add(Card(layout));
    }

    private void ShowExceptions()
    {
        pageTitle.Text = "الاستثناءات";
        AddCard("متاحة في الأنشطة", "تتجاوز الحجب المجدول فقط. الحجب الدائم له أولوية ولا يتأثر بالاستثناءات.");
        var data = store.Load();
        AddEntryList("تطبيقات مسموحة", false);
        AddEntryList("مواقع مسموحة", true);
    }

    private void AddEntryList(string title, bool site)
    {
        var data = store.Load(); var values = site ? data.ExceptionSites : data.ExceptionApps;
        AddSection(title);
        AddCard(title, values.Count == 0 ? "لا توجد عناصر." : string.Join("\n", values));
        var button = ActionButton("إدارة القائمة", Accent);
        button.Click += async (_, _) =>
        {
            var textBox = new TextBox { Text = string.Join(Environment.NewLine, values), AcceptsReturn = true, MinHeight = 130, PlaceholderText = site ? "نطاق في كل سطر مثل example.com" : "اسم العملية في كل سطر مثل firefox" };
            var saveButton = ActionButton("حفظ", Accent); var cancel = ActionButton("إلغاء", "#303646");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var dialog = Dialog(title, 430); var body = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
            body.Children.Add(textBox); buttons.Children.Add(saveButton); buttons.Children.Add(cancel); body.Children.Add(buttons); dialog.Content = body;
            saveButton.Click += (_, _) =>
            {
                var latest = store.Load(); var parsed = ParseLines(textBox.Text, site);
                if (site) latest.ExceptionSites = parsed; else latest.ExceptionApps = parsed;
                store.Save(latest); dialog.Close(true);
            };
            cancel.Click += (_, _) => dialog.Close(false);
            if (await dialog.ShowDialog<bool>(this)) ShowPage(page);
        };
        content.Children.Add(button);
    }

    private void ShowProtection()
    {
        pageTitle.Text = "التشغيل والحماية";
        AddCard("خدمة الحماية", "بعد فك حزمة Linux شغّل install.sh مرة واحدة مع sudo. ستعمل الخدمة في الخلفية عند إغلاق StudyGuard.");
        AddCard("حجب التطبيقات", "يحتاج وكيل النظام صلاحية إيقاف عمليات المستخدم. يرسل إشارة إغلاق عادية، ثم ينهي التطبيق بعد مهلة قصيرة إذا بقي مفتوحًا.");
        AddCard("حجب المواقع", "تُطبّق المواقع على مستوى الجهاز عبر /etc/hosts. يتطلب ذلك تثبيت وكيل النظام بصلاحية مسؤول.");
        AddCard("متطلبات", "النسخة مخصصة لـLinux x64 مع systemd وواجهة رسومية X11 أو Wayland عبر XWayland. اختبرها أولًا على Ubuntu/Debian.");
    }

    private async Task<bool> ActivityEditor(ActivityRule? source)
    {
        var rule = source is null ? new ActivityRule() : JsonSerializer.Deserialize<ActivityRule>(JsonSerializer.Serialize(source))!;
        var form = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        var name = new TextBox { Text = rule.Name, PlaceholderText = "اسم النشاط" };
        var start = new TextBox { Text = rule.Start, PlaceholderText = "البداية 07:00" };
        var end = new TextBox { Text = rule.End, PlaceholderText = "النهاية 08:00" };
        form.Children.Add(name); form.Children.Add(Label("الأيام", 14, true));
        var days = new WrapPanel { Orientation = Orientation.Horizontal };
        var dayChecks = new List<CheckBox>();
        for (var i = 0; i < StudyStore.DayNames.Length; i++) { var check = new CheckBox { Content = StudyStore.DayNames[i], IsChecked = rule.Days.Contains(i), Margin = new Thickness(4) }; days.Children.Add(check); dayChecks.Add(check); }
        form.Children.Add(days);
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; times.Children.Add(start); times.Children.Add(end); form.Children.Add(times);
        var blockApps = new CheckBox { Content = "حجب التطبيقات المذكورة", IsChecked = rule.BlockApps };
        var blockSites = new CheckBox { Content = "حجب المواقع المذكورة", IsChecked = rule.BlockSites };
        form.Children.Add(blockApps); form.Children.Add(blockSites);
        var apps = new TextBox { Text = string.Join(Environment.NewLine, rule.Apps), AcceptsReturn = true, MinHeight = 88, PlaceholderText = "اسم العملية لكل تطبيق، مثل firefox" };
        var sites = new TextBox { Text = string.Join(Environment.NewLine, rule.Sites), AcceptsReturn = true, MinHeight = 88, PlaceholderText = "اسم النطاق لكل موقع، مثل youtube.com" };
        form.Children.Add(apps); form.Children.Add(sites);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var save = ActionButton("حفظ", Accent); var cancel = ActionButton("إلغاء", "#303646"); actions.Children.Add(save); actions.Children.Add(cancel); form.Children.Add(actions);
        var dialog = Dialog(source is null ? "إضافة نشاط" : "تعديل نشاط", 680); dialog.Content = new ScrollViewer { Content = form };
        save.Click += (_, _) =>
        {
            var selectedDays = dayChecks.Select((check, index) => (check, index)).Where(x => x.check.IsChecked == true).Select(x => x.index).ToList();
            if (string.IsNullOrWhiteSpace(name.Text) || selectedDays.Count == 0 || !TimeSpan.TryParse(start.Text, out _) || !TimeSpan.TryParse(end.Text, out _) || start.Text == end.Text)
            { status.Text = "اكتب اسمًا ووقتًا صحيحًا واختار يومًا واحدًا على الأقل."; return; }
            rule.Name = name.Text.Trim(); rule.Start = start.Text.Trim(); rule.End = end.Text.Trim(); rule.Days = selectedDays;
            rule.BlockApps = blockApps.IsChecked == true; rule.BlockSites = blockSites.IsChecked == true;
            rule.Apps = ParseLines(apps.Text, false); rule.Sites = ParseLines(sites.Text, true);
            var data = store.Load(); var index = data.Activities.FindIndex(x => x.Id == rule.Id);
            if (index < 0) data.Activities.Add(rule); else data.Activities[index] = rule;
            store.Save(data); dialog.Close(true);
        };
        cancel.Click += (_, _) => dialog.Close(false);
        var saved = await dialog.ShowDialog<bool>(this); if (saved) ShowPage("schedule"); return saved;
    }

    private async Task<TimedEntry?> EntryDialog(string title, bool site)
    {
        var input = new TextBox { PlaceholderText = site ? "example.com" : "اسم العملية مثل firefox", HorizontalContentAlignment = HorizontalAlignment.Right };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var save = ActionButton("إضافة", Accent); var cancel = ActionButton("إلغاء", "#303646"); actions.Children.Add(save); actions.Children.Add(cancel);
        var body = new StackPanel { Margin = new Thickness(18), Spacing = 12 }; body.Children.Add(input); body.Children.Add(actions);
        var dialog = Dialog(title, 220); dialog.Content = body; TimedEntry? result = null;
        save.Click += (_, _) =>
        {
            var value = site ? StudyStore.NormalizeDomain(input.Text) : NormalizeProcessName(input.Text);
            if (value.Length == 0) { status.Text = site ? "اكتب نطاقًا صحيحًا." : "اكتب اسم عملية صحيحًا."; return; }
            var data = store.Load(); var list = site ? data.PermanentSites : data.PermanentApps;
            if (!list.Any(x => x.Value.Equals(value, StringComparison.OrdinalIgnoreCase))) list.Add(new TimedEntry { Value = value, AddedAt = DateTimeOffset.UtcNow });
            store.Save(data); result = list.First(x => x.Value.Equals(value, StringComparison.OrdinalIgnoreCase)); dialog.Close(true);
        };
        cancel.Click += (_, _) => dialog.Close(false);
        await dialog.ShowDialog<bool>(this); return result;
    }

    private void AddCard(string title, string body) { var stack = new StackPanel { Spacing = 6 }; stack.Children.Add(Label(title, 15, true)); stack.Children.Add(Label(body, 14)); content.Children.Add(Card(stack)); }
    private void AddSection(string title) => content.Children.Add(Label(title, 18, true));
    private static Border Card(Control child) => new() { Background = Brush(CardBg), BorderBrush = Brush("#303747"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Child = child };
    private static TextBlock Label(string text, double size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Foreground = Brush(bold ? TextColor : Muted), TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.RightToLeft, TextAlignment = TextAlignment.Right };
    private static Button ActionButton(string text, string color) => new() { Content = text, Background = Brush(color), Foreground = Brush(TextColor), Padding = new Thickness(14, 9), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
    private Window Dialog(string title, double height) => new() { Title = title, Width = 560, Height = height, MinHeight = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush(Bg), FlowDirection = FlowDirection.RightToLeft };
    private static List<string> ParseLines(string? text, bool domains) => (text ?? "").Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => domains ? StudyStore.NormalizeDomain(value) : NormalizeProcessName(value)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static string NormalizeProcessName(string? value)
    {
        var name = (value ?? "").Trim(); name = Path.GetFileName(name);
        return name.Length <= 120 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') ? name : "";
    }
    private static string PolicyText(ActivityRule rule)
    {
        var items = new List<string>(); if (rule.BlockApps) items.Add("حجب التطبيقات: " + string.Join("، ", rule.Apps)); if (rule.BlockSites) items.Add("حجب المواقع: " + string.Join("، ", rule.Sites));
        return items.Count == 0 ? "الحجب غير مفعّل في النشاط." : string.Join(" · ", items);
    }
    private static (ActivityRule Rule, int Day)? NextActivity(StudyData data, DateTimeOffset now)
    {
        var current = now.LocalDateTime;
        return data.Activities.SelectMany(rule => rule.Days.Select(day => (Rule: rule, Day: day)))
            .Select(item => (item.Rule, item.Day, Start: NextStart(item.Rule, item.Day, current)))
            .Where(item => item.Start > current).OrderBy(item => item.Start)
            .Select(item => ((ActivityRule Rule, int Day)?)(item.Rule, item.Day)).FirstOrDefault();
    }
    private static DateTime NextStart(ActivityRule rule, int day, DateTime now)
    {
        var date = now.Date.AddDays((day - (int)now.DayOfWeek + 7) % 7);
        var start = TimeSpan.TryParse(rule.Start, out var parsed) ? parsed : TimeSpan.Zero;
        var value = date.Add(start); return value <= now ? value.AddDays(7) : value;
    }
}
