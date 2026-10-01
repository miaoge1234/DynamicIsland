using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>设置倒计时或闹钟的小窗口，外观和灵动岛一致。</summary>
public partial class TimerWindow : Window
{
    public TimerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            MinuteBox.Focus();
            MinuteBox.SelectAll();
        };
    }

    /// <summary>确认后交给调用方去启动。</summary>
    public Action<IslandTimerKind, TimeSpan, int>? Confirmed { get; set; }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (CountdownPanel is null || AlarmPanel is null)
        {
            return;
        }

        bool countdown = CountdownRadio.IsChecked == true;
        CountdownPanel.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        AlarmPanel.Visibility = countdown ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = string.Empty;
    }

    private void OnQuickPick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int minutes))
        {
            return;
        }

        HourBox.Text = (minutes / 60).ToString();
        MinuteBox.Text = (minutes % 60).ToString();
        SecondBox.Text = "0";
        ErrorText.Text = string.Empty;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (CountdownRadio.IsChecked == true)
        {
            int hours = ParseClamped(HourBox.Text, 0, 99);
            int minutes = ParseClamped(MinuteBox.Text, 0, 999);
            int seconds = ParseClamped(SecondBox.Text, 0, 999);

            var total = new TimeSpan(hours, 0, 0) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            if (total <= TimeSpan.Zero)
            {
                ErrorText.Text = "时长要大于 0。";
                return;
            }

            Confirmed?.Invoke(IslandTimerKind.Countdown, total, 0);
            Close();
            return;
        }

        if (!int.TryParse(AlarmHourBox.Text.Trim(), out int alarmHour) || alarmHour < 0 || alarmHour > 23 ||
            !int.TryParse(AlarmMinuteBox.Text.Trim(), out int alarmMinute) || alarmMinute < 0 || alarmMinute > 59)
        {
            ErrorText.Text = "请输入 0-23 时、0-59 分。";
            return;
        }

        Confirmed?.Invoke(IslandTimerKind.Alarm, TimeSpan.Zero, alarmHour * 60 + alarmMinute);
        Close();
    }

    private static int ParseClamped(string text, int min, int max)
        => int.TryParse(text.Trim(), out int value) ? Math.Clamp(value, min, max) : 0;

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
