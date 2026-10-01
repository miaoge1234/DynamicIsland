using System.Windows;
using System.Windows.Input;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// 设置窗口。改完直接写回 settings.json，外观和开关即时生效（端口要重启）。
/// 外观跟灵动岛一套皮：无边框深色玻璃 + 圆角 + 金色强调。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;
        LoadValues();
        _loading = false;

        // 无边框窗口的圆角交给 DWM，和系统其它窗口一致
        Loaded += (_, _) => WindowChrome.ApplyRoundedCorners(this);
    }

    private void LoadValues()
    {
        TopOffsetSlider.Value = Math.Clamp(_settings.TopOffset, TopOffsetSlider.Minimum, TopOffsetSlider.Maximum);
        BlurSlider.Value = Math.Clamp(_settings.GlassBlurRadius, BlurSlider.Minimum, BlurSlider.Maximum);
        TintSlider.Value = Math.Clamp(_settings.GlassTintOpacity, TintSlider.Minimum, TintSlider.Maximum);
        HoldSlider.Value = Math.Clamp(_settings.MessageHoldMs, HoldSlider.Minimum, HoldSlider.Maximum);
        RefreshSlider.Value = Math.Clamp(_settings.BackdropRefreshMs, RefreshSlider.Minimum, RefreshSlider.Maximum);
        WeatherRefreshSlider.Value = Math.Clamp(
            _settings.WeatherRefreshMinutes, WeatherRefreshSlider.Minimum, WeatherRefreshSlider.Maximum);
        ScaleSlider.Value = Math.Clamp(_settings.IslandScale, ScaleSlider.Minimum, ScaleSlider.Maximum);

        ShowMusicCheck.IsChecked = _settings.EnableMusic;
        ShowWeatherCheck.IsChecked = _settings.EnableWeather;
        ShowMessagesCheck.IsChecked = _settings.EnableWindowsNotifications;
        ShowTodosCheck.IsChecked = _settings.ShowTodos;
        ShowPerformanceCheck.IsChecked = _settings.ShowPerformance;
        PerfInPillCheck.IsChecked = _settings.ShowPerformanceInPill;
        MemoryDetailCheck.IsChecked = _settings.ShowMemoryDetail;

        QqCallCheck.IsChecked = _settings.EnableQqWatch;
        ExcludeCheck.IsChecked = _settings.ExcludeFromCapture;
        AutoLocateCheck.IsChecked = _settings.AutoLocate;

        // 自启以注册表实际状态为准
        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        _settings.StartWithWindows = AutoStart.IsEnabled();

        CityBox.Text = _settings.City;
        PortBox.Text = _settings.PushPort.ToString();

        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        TopOffsetValue.Text = $"{TopOffsetSlider.Value:0} px";
        BlurValue.Text = $"{BlurSlider.Value:0}";
        TintValue.Text = $"{TintSlider.Value:0.00}";
        HoldValue.Text = $"{HoldSlider.Value / 1000.0:0.0} 秒";
        RefreshValue.Text = RefreshSlider.Value <= 0 ? "关闭" : $"{RefreshSlider.Value / 1000.0:0.0} 秒";
        WeatherRefreshValue.Text = $"{WeatherRefreshSlider.Value:0} 分";
        ScaleValue.Text = $"{(int)Math.Round(ScaleSlider.Value * 100)}%";
    }

    private void OnAppearanceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 必须先判断加载中再碰任何控件：XAML 里给 Slider 设 Minimum 会触发 ValueChanged，
        // 那时排在它后面的标签控件还没创建，先更新标签就会空引用崩掉。
        if (_loading)
        {
            return;
        }

        UpdateValueLabels();

        _settings.TopOffset = TopOffsetSlider.Value;
        _settings.GlassBlurRadius = BlurSlider.Value;
        _settings.GlassTintOpacity = TintSlider.Value;
        _settings.MessageHoldMs = (int)HoldSlider.Value;
        _settings.BackdropRefreshMs = (int)RefreshSlider.Value;
        _settings.WeatherRefreshMinutes = (int)WeatherRefreshSlider.Value;
        _settings.IslandScale = ScaleSlider.Value;
        _settings.Save();
        HintText.Text = "已保存";
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EnableMusic = ShowMusicCheck.IsChecked == true;
        _settings.EnableWeather = ShowWeatherCheck.IsChecked == true;
        _settings.EnableWindowsNotifications = ShowMessagesCheck.IsChecked == true;
        _settings.ShowTodos = ShowTodosCheck.IsChecked == true;
        _settings.ShowPerformance = ShowPerformanceCheck.IsChecked == true;
        _settings.ShowPerformanceInPill = PerfInPillCheck.IsChecked == true;
        _settings.ShowMemoryDetail = MemoryDetailCheck.IsChecked == true;

        _settings.EnableQqWatch = QqCallCheck.IsChecked == true;
        _settings.ExcludeFromCapture = ExcludeCheck.IsChecked == true;
        _settings.AutoLocate = AutoLocateCheck.IsChecked == true;
        _settings.City = CityBox.Text.Trim();

        // 开机自启：同时写注册表
        bool wantAutoStart = AutoStartCheck.IsChecked == true;
        if (wantAutoStart != _settings.StartWithWindows)
        {
            _settings.StartWithWindows = wantAutoStart;
            bool ok = AutoStart.Apply(wantAutoStart);
            HintText.Text = ok
                ? (wantAutoStart ? "已设置开机自启" : "已关闭开机自启")
                : "自启设置失败（注册表不可写）";
        }
        else
        {
            HintText.Text = "已保存";
        }

        _settings.Save();
    }

    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        _settings.WindowLeft = null;
        _settings.WindowTop = null;
        _settings.Save();
        HintText.Text = "已复位，关闭本窗口后岛会回到顶部居中";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PortBox.Text.Trim(), out int port) && port >= 0 && port <= 65535)
        {
            if (port != _settings.PushPort)
            {
                _settings.PushPort = port;
                HintText.Text = "端口已保存，重启后生效";
            }
        }
        else
        {
            PortBox.Text = _settings.PushPort.ToString();
            HintText.Text = "端口不合法，已还原";
        }

        _settings.AutoLocate = AutoLocateCheck.IsChecked == true;
        _settings.City = CityBox.Text.Trim();
        _settings.Save();
        Close();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        _settings.TopOffset = defaults.TopOffset;
        _settings.GlassBlurRadius = defaults.GlassBlurRadius;
        _settings.GlassTintOpacity = defaults.GlassTintOpacity;
        _settings.MessageHoldMs = defaults.MessageHoldMs;
        _settings.IslandScale = defaults.IslandScale;
        _settings.ShowTodos = defaults.ShowTodos;
        _settings.ShowPerformance = defaults.ShowPerformance;
        _settings.ShowPerformanceInPill = defaults.ShowPerformanceInPill;
        _settings.ShowMemoryDetail = defaults.ShowMemoryDetail;
        _settings.BackdropRefreshMs = defaults.BackdropRefreshMs;
        _settings.WeatherRefreshMinutes = defaults.WeatherRefreshMinutes;
        _settings.PushPort = defaults.PushPort;
        _settings.EnableMusic = defaults.EnableMusic;
        _settings.EnableWeather = defaults.EnableWeather;
        _settings.EnableWindowsNotifications = defaults.EnableWindowsNotifications;
        _settings.EnableQqWatch = defaults.EnableQqWatch;
        _settings.ExcludeFromCapture = defaults.ExcludeFromCapture;
        _settings.WindowLeft = null;
        _settings.WindowTop = null;
        _settings.City = string.Empty;
        _settings.AutoLocate = defaults.AutoLocate;
        _settings.Save();

        _loading = true;
        LoadValues();
        _loading = false;
        HintText.Text = "已恢复默认（端口改动重启生效）";
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseChrome(object sender, RoutedEventArgs e) => Close();
}
