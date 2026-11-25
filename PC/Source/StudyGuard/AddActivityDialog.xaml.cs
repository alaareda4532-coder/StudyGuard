using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace StudyGuard;

public partial class AddActivityDialog : Window
{
    public string ActivityName { get; private set; } = "";
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }
    public List<SelectedDay> SelectedDays { get; private set; } = new();

    public AddActivityDialog(string initiallySelectedDayTag)
    {
        InitializeComponent();
        InitializeTimePickers();
        SetTimePicker(StartHourComboBox, StartMinuteComboBox, StartPeriodComboBox, new TimeOnly(8, 0));
        SetTimePicker(EndHourComboBox, EndMinuteComboBox, EndPeriodComboBox, new TimeOnly(9, 0));

        foreach (CheckBox dayCheckBox in GetDayCheckBoxes())
        {
            dayCheckBox.IsChecked = string.Equals(
                dayCheckBox.Tag?.ToString(),
                initiallySelectedDayTag,
                StringComparison.Ordinal);
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ActivityNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            DialogErrorText.Text = "اكتب اسم النشاط.";
            ActivityNameTextBox.Focus();
            return;
        }

        if (!TryReadTimePicker(StartHourComboBox, StartMinuteComboBox, StartPeriodComboBox, out TimeOnly startTime))
        {
            DialogErrorText.Text = "اختار الساعة والدقيقة والفترة لوقت البداية.";
            StartHourComboBox.Focus();
            return;
        }

        if (!TryReadTimePicker(EndHourComboBox, EndMinuteComboBox, EndPeriodComboBox, out TimeOnly endTime))
        {
            DialogErrorText.Text = "اختار الساعة والدقيقة والفترة لوقت النهاية.";
            EndHourComboBox.Focus();
            return;
        }

        if (startTime == endTime)
        {
            DialogErrorText.Text = "وقت البداية والنهاية لا يمكن أن يكونا متساويين.";
            return;
        }

        SelectedDays = GetDayCheckBoxes()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => new SelectedDay(
                checkBox.Tag?.ToString() ?? "",
                checkBox.Content?.ToString() ?? ""))
            .Where(day => !string.IsNullOrWhiteSpace(day.Tag))
            .ToList();

        if (SelectedDays.Count == 0)
        {
            DialogErrorText.Text = "اختار يومًا واحدًا على الأقل.";
            return;
        }

        ActivityName = name;
        StartTime = startTime;
        EndTime = endTime;
        DialogResult = true;
    }

    private IEnumerable<CheckBox> GetDayCheckBoxes()
    {
        return new[]
        {
            SaturdayCheckBox,
            SundayCheckBox,
            MondayCheckBox,
            TuesdayCheckBox,
            WednesdayCheckBox,
            ThursdayCheckBox,
            FridayCheckBox
        };
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
    }

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

    public sealed record SelectedDay(string Tag, string Name);
}
