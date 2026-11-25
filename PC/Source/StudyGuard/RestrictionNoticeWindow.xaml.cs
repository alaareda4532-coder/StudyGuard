using System;
using System.Windows;
using System.Windows.Threading;

namespace StudyGuard;

public partial class RestrictionNoticeWindow : Window
{
    private readonly DispatcherTimer closeTimer = new()
    {
        Interval = TimeSpan.FromSeconds(8)
    };

    public RestrictionNoticeWindow()
    {
        InitializeComponent();
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
        Loaded += (_, _) =>
        {
            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 24;
            Top = workArea.Bottom - Height - 24;
        };
        Closed += (_, _) => closeTimer.Stop();
    }

    public void SetNotice(string activityName, string blockedItem)
    {
        NoticeTitleText.Text = $"يا بطل، أنت في وضع «{activityName}»";
        NoticeBodyText.Text = $"«{blockedItem}» مش مسموح دلوقتي. ركّز في اللي بدأته، وارجع له بعد ما يخلص الوقت.";
        closeTimer.Stop();
        closeTimer.Start();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();
}
