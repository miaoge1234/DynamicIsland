using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Models;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    // ---------- 岛的几何尺寸（DIP） ----------
    /// <summary>Host 的固定宽度 = 展开态的岛。动画只改剪裁，不改它。</summary>
    private const double IslandMaxWidth = 560;

    private const double CollapsedWidth = 232;
    private const double CollapsedHeight = 42;
    private const double CollapsedRadius = 21;

    private const double ExpandedRadius = 28;

    /// <summary>展开态的行高（关掉某些卡片时会被算小）</summary>
    private const double HeaderHeight = 62;
    private const double MusicHeight = 76;
    private const double MessagesHeight = 102;
    private const double RowGap = 8;
    private const double PanelPadding = 36;

    /// <summary>展开态的实际高度，跟着"显示哪些卡片"变</summary>
    private double _islandHeight = 292;

    /// <summary>岛的整体缩放（自定义大小）</summary>
    private double _scale = 1.0;

    private static Rect ExpandedRect(double height) => new(0, 0, IslandMaxWidth, height);

    /// <summary>收起时胶囊的位置：水平居中、贴着 Host 顶部</summary>
    private static Rect CollapsedRect => new(
        (IslandMaxWidth - CollapsedWidth) / 2, 0, CollapsedWidth, CollapsedHeight);

    /// <summary>岛距离窗口顶部留多少，给描边和模糊留余量</summary>
    private const double HostTopMargin = 16;

    /// <summary>点天气打开的网页</summary>
    private const string WeatherUrl = "https://www.msn.cn/zh-cn/weather/";

    /// <summary>抓屏时向外多抓多少，保证模糊到边缘时不会"饿死"</summary>
    private const double CaptureMarginDip = 40;

    // ---------- 服务 ----------
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ScreenCapture _capture = new();
    private readonly MessageHub _hub;
    private readonly NowPlayingService _music;
    private readonly WeatherService _weather;
    private readonly QqWatcher _qq;
    private readonly PushServer _push;
    private readonly NotificationService _notifications;
    private readonly TimerService _timer;
    private readonly SystemMonitorService _monitor;
    private readonly TodoService _todos = new();

    // ---------- Win32 ----------
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private ShellHook? _shellHook;
    private double _dpiScale = 1;

    // ---------- 背景抓屏状态 ----------
    private readonly RectangleGeometry _glassClip = new();
    private Int32Rect _capturedRect;
    private bool _hasCapture;
    private DateTime _lastCaptureUtc = DateTime.MinValue;

    // ---------- 定时器 ----------
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _backdropTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _timerResetTimer;

    // ---------- 状态 ----------
    private bool _isExpanded;
    private bool _isTransitioning;
    private bool _contextMenuOpen;
    private bool _isSeeking;
    private bool _showingTodos;

    // 拖动灵动岛
    private bool _isDragging;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private bool _dragMoved;

    private IntPtr _actionTargetWindow = IntPtr.Zero;
    private readonly List<IslandMessage> _recent = new();
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private Storyboard? _equalizer;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();

        _hub = new MessageHub(Dispatcher);
        _music = new NowPlayingService(Dispatcher);
        _weather = new WeatherService(_settings);
        _qq = new QqWatcher(Dispatcher, _settings);
        _push = new PushServer(_hub, _settings.PushPort);
        _notifications = new NotificationService(_settings, Dispatcher);
        _timer = new TimerService(Dispatcher);
        _monitor = new SystemMonitorService(Dispatcher);

        // 玻璃参数来自配置。模糊用 CPU 盒子模糊做，半径按缩小倍数换算。
        _capture.Downscale = 6;
        _capture.BlurRadius = Math.Clamp((int)Math.Round(_settings.GlassBlurRadius / 8.0), 1, 8);
        _capture.BlurPasses = 2;
        TintRect.Opacity = Math.Clamp(_settings.GlassTintOpacity, 0, 1);

        Glass.Clip = _glassClip;
        _glassClip.RadiusX = CollapsedRadius;
        _glassClip.RadiusY = CollapsedRadius;

        BackdropImage.Visibility = Visibility.Collapsed;
        TintRect.Fill = BuildFallbackTint();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _backdropTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200, _settings.BackdropRefreshMs)),
        };
        _backdropTimer.Tick += (_, _) => UpdateBackdrop(forceCapture: true);

        _collapseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            // 这个定时器现在只管"新消息来了之后岛停留多久"，鼠标离开是立刻收的
            Interval = TimeSpan.FromMilliseconds(Math.Max(600, _settings.MessageHoldMs)),
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_contextMenuOpen && !Host.IsMouseOver)
            {
                SetExpanded(false);
            }
        };

        // "时间到"的状态显示一会儿就恢复成普通时钟
        _timerResetTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(12),
        };
        _timerResetTimer.Tick += (_, _) =>
        {
            _timerResetTimer.Stop();
            ApplyTimerState();
        };

        Host.MouseEnter += OnHostMouseEnter;
        Host.MouseLeave += OnHostMouseLeave;

        // 拖动灵动岛（按住岛身空白处拖）
        Glass.MouseLeftButtonDown += OnIslandMouseDown;
        Glass.MouseMove += OnIslandMouseMove;
        Glass.MouseLeftButtonUp += OnIslandMouseUp;

        TimerButton.Click += (_, _) => OpenTimerDialog();
        SettingsButton.Click += (_, _) => OpenSettings();

        _todos.Changed += (_, _) => RefreshTodoView();

        PrevButton.Click += async (_, _) => await _music.PreviousAsync();
        PlayPauseButton.Click += async (_, _) => await _music.TogglePlayPauseAsync();
        NextButton.Click += async (_, _) => await _music.NextAsync();

        // 拖动进度条跳转
        ProgressHit.MouseLeftButtonDown += OnProgressMouseDown;
        ProgressHit.MouseMove += OnProgressMouseMove;
        ProgressHit.MouseLeftButtonUp += OnProgressMouseUp;

        // 服务事件
        _music.TrackChanged += (_, track) => ApplyTrack(track);
        _music.ProgressTick += (_, track) => ApplyProgress(track);
        _weather.Updated += (_, info) => ApplyWeather(info);
        _qq.Detected += (_, message) => HandleIncoming(message);
        _hub.MessageAdded += (_, message) => HandleIncoming(message);
        _notifications.Changed += (_, list) => RebuildMessageList(list);
        _notifications.Added += (_, message) => HandleIncoming(message);

        _timer.Tick2 += (_, _) => ApplyTimerState();
        _timer.Finished += (_, _) => OnTimerFinished();
        _monitor.Updated += (_, _) => ApplyPerformance();

        // 点天气打开天气网页
        WeatherBlock.MouseLeftButtonUp += (_, _) => OpenWeatherPage();

        MessageScroll.ScrollChanged += (_, e) => UpdateMoreHint(e.ExtentHeight, e.ViewportHeight, e.VerticalOffset);
        MessageList.MouseLeftButtonUp += OnMessageListClick;

        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _closing = true;
            Shutdown();
        };
    }

    // ==================================================================
    //  生命周期
    // ==================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        // 不进 Alt+Tab、不抢焦点
        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // 让抓屏抓不到自己，否则背景会把自己的画面反复模糊（自我递归）
        if (_settings.ExcludeFromCapture)
        {
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        // Shell 钩子：QQ 收到消息时会请求注意（窗口闪烁）
        if (_hwndSource is not null)
        {
            _shellHook = new ShellHook(_hwndSource);
            _shellHook.WindowFlash += OnWindowFlash;
            _shellHook.Register();
        }
    }

    private void OnWindowFlash(IntPtr hwnd) => _qq.NotifyFlash(hwnd);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // 开机的自启项和设置对一遍
        AutoStart.SyncOnStartup(_settings.StartWithWindows);

        ApplyLayoutOptions();
        PositionWindow();

        _todos.Load();
        _todos.Save();
        TodoList.ItemsSource = _todos.Items;

        // 启动时明确停在"消息"页，把页签选中态和空状态都摆正
        SwitchView(showTodos: false);

        BuildContextMenu();
        ApplyCollapsedGeometry();

        UpdateClock();
        _clockTimer.Start();

        UpdateBackdrop(forceCapture: true);
        if (_settings.BackdropRefreshMs > 0)
        {
            _backdropTimer.Start();
        }

        _qq.Start();
        _push.Start();
        _notifications.Start();
        _monitor.Start();

        if (_settings.EnableWeather)
        {
            _weather.Start();
        }
        else
        {
            WeatherDesc.Text = "天气已关闭";
        }

        if (_settings.EnableMusic)
        {
            await _music.InitializeAsync();
        }
        else
        {
            TrackTitle.Text = "音乐已关闭";
        }

        ApplyTrack(_music.Current);
        RebuildMessageList(_notifications.Current);
        ApplyTimerState();

        if (Diagnostics.Enabled)
        {
            _ = RunDiagnosticsAsync();
        }

        if (Environment.GetEnvironmentVariable("DSH_ISLAND_OPEN_SETTINGS") == "1")
        {
            Diagnostics.Write("OnLoaded: 准备打开设置窗口");
            OpenSettings();
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        try
        {
            await Task.Delay(2500);
            Diagnostics.LogGeometry(
                _hwnd,
                new Point((Width - Host.ActualWidth) / 2.0, HostTopMargin),
                new Size(Host.ActualWidth, Host.ActualHeight),
                _dpiScale);

            if (Diagnostics.ExitAfter)
            {
                await Task.Delay(200);
                Close();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write("RunDiagnosticsAsync 失败: " + ex.Message);
        }
    }

    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        int screenWidth = NativeMethods.GetSystemMetrics(0);
        int screenHeight = NativeMethods.GetSystemMetrics(1);
        int physWidth = (int)Math.Round(Width * _dpiScale);
        int physHeight = (int)Math.Round(Height * _dpiScale);

        int x;
        int y;

        if (_settings.WindowLeft is double savedLeft && _settings.WindowTop is double savedTop)
        {
            // 拖过就按记住的位置放，并且保证还在屏幕里
            x = (int)Math.Round(savedLeft * _dpiScale);
            y = (int)Math.Round(savedTop * _dpiScale);
            x = Math.Clamp(x, 0, Math.Max(0, screenWidth - physWidth));
            y = Math.Clamp(y, 0, Math.Max(0, screenHeight - physHeight));
        }
        else
        {
            x = Math.Max(0, (screenWidth - physWidth) / 2);
            y = (int)Math.Round(Math.Max(0, _settings.TopOffset) * _dpiScale);
        }

        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST,
            x, y, physWidth, physHeight,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    // ==================================================================
    //  拖动灵动岛
    // ==================================================================

    private void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮、进度条、列表、天气块上的时候不算拖动
        if (IsInteractiveSource(e.OriginalSource as DependencyObject) ||
            IsWeatherBlock(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isDragging = true;
        _dragMoved = false;

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            _dragStartScreen = new Point(cursor.X, cursor.Y);
        }

        // 用真实窗口矩形当起点，别信 WPF 的 Left/Top（被外部移动时它可能滞后）
        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var startRect))
        {
            _dragStartLeft = startRect.Left / _dpiScale;
            _dragStartTop = startRect.Top / _dpiScale;
        }

        Glass.CaptureMouse();
        _collapseTimer.Stop();
    }

    private void OnIslandMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        double dx = (cursor.X - _dragStartScreen.X) / _dpiScale;
        double dy = (cursor.Y - _dragStartScreen.Y) / _dpiScale;

        if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
        {
            return;
        }

        _dragMoved = true;

        double targetLeft = _dragStartLeft + dx;
        double targetTop = _dragStartTop + dy;

        // 拖的时候用 SetWindowPos，比改 Left/Top 更跟手
        if (_hwnd != IntPtr.Zero)
        {
            int physWidth = (int)Math.Round(Width * _dpiScale);
            int physHeight = (int)Math.Round(Height * _dpiScale);
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST,
                (int)Math.Round(targetLeft * _dpiScale),
                (int)Math.Round(targetTop * _dpiScale),
                physWidth, physHeight,
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void OnIslandMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        Glass.ReleaseMouseCapture();

        if (_dragMoved)
        {
            // 记住新位置（同样以真实窗口矩形为准）
            if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var endRect))
            {
                _settings.WindowLeft = endRect.Left / _dpiScale;
                _settings.WindowTop = endRect.Top / _dpiScale;
                _settings.Save();
            }

            UpdateBackdrop(forceCapture: true);
        }
        else
        {
            MaybeStartCollapseTimer();
        }
    }

    /// <summary>这个点击源是不是可交互控件（按钮、进度条、滚动列表）。</summary>
    private static bool IsInteractiveSource(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is ButtonBase or ScrollViewer or ProgressBar)
            {
                return true;
            }

            if (current is FrameworkElement { Name: "ProgressHit" })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Shutdown()
    {
        _clockTimer.Stop();
        _backdropTimer.Stop();
        _collapseTimer.Stop();
        _timerResetTimer.Stop();
        _shellHook?.Dispose();
        _qq.Dispose();
        _music.Dispose();
        _weather.Dispose();
        _push.Dispose();
        _notifications.Dispose();
        _timer.Dispose();
        _monitor.Dispose();
    }

    // ==================================================================
    //  Win32 钩子
    // ==================================================================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                {
                    // 只有胶囊范围内接收鼠标；外面全部穿透，不挡用户操作
                    long lp = lParam.ToInt64();
                    int sx = (short)(lp & 0xFFFF);
                    int sy = (short)((lp >> 16) & 0xFFFF);
                    if (!IsPointOnIsland(sx, sy))
                    {
                        handled = true;
                        return new IntPtr(NativeMethods.HTTRANSPARENT);
                    }
                    break;
                }

            case NativeMethods.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                Dispatcher.BeginInvoke(() =>
                {
                    PositionWindow();
                    UpdateBackdrop(forceCapture: true);
                }, DispatcherPriority.Background);
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsPointOnIsland(int screenX, int screenY)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(_hwnd, out var windowRect))
        {
            return false;
        }

        double dpi = _dpiScale <= 0 ? 1 : _dpiScale;
        double k = _scale;
        double origin = IslandMaxWidth / 2.0;
        double hostLeft = (Width - IslandMaxWidth) / 2.0;

        // 岛的可见范围 = 当前剪裁形状（host 局部坐标），再套上整体缩放
        Rect shape = _glassClip.Rect;

        double visualX = origin + k * (shape.X - origin);
        double visualY = k * shape.Y;
        double w = shape.Width * k;
        double h = shape.Height * k;

        double left = windowRect.Left + (hostLeft + visualX) * dpi;
        double top = windowRect.Top + (HostTopMargin + visualY) * dpi;
        double width = w * dpi;
        double height = h * dpi;
        double r = Math.Max(0, _glassClip.RadiusX) * k * dpi;

        double px = screenX - left;
        double py = screenY - top;

        if (px < 0 || py < 0 || px > width || py > height)
        {
            return false;
        }

        if (r <= 0.5)
        {
            return true;
        }

        double cx = px < r ? r : px > width - r ? width - r : px;
        double cy = py < r ? r : py > height - r ? height - r : py;
        double dx = px - cx;
        double dy = py - cy;
        return (dx * dx + dy * dy) <= r * r + 1;
    }

    // ==================================================================
    //  背景：抓屏 + 模糊（"液态玻璃"的来源）
    // ==================================================================

    private static Brush BuildFallbackTint()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.4, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, 0x2A, 0x2C, 0x36), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xD8, 0x17, 0x18, 0x1E), 1));
        brush.Freeze();
        return brush;
    }

    private void UpdateBackdrop(bool forceCapture)
    {
        if (_closing || _hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var windowRect))
        {
            return;
        }

        double s = _dpiScale <= 0 ? 1 : _dpiScale;
        int margin = (int)Math.Round(CaptureMarginDip * s);

        var wanted = new Int32Rect(
            windowRect.Left - margin,
            windowRect.Top - margin,
            windowRect.Width + margin * 2,
            windowRect.Height + margin * 2);

        var virtualScreen = ScreenCapture.VirtualScreenBounds();
        var clamped = Intersect(wanted, virtualScreen);
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            return;
        }

        // 展开/收起动画期间只跟着挪位置，不做抓屏，保证动画满帧
        if (_isTransitioning && !forceCapture)
        {
            RepositionBackdrop(windowRect, s);
            return;
        }

        bool rectChanged =
            clamped.X != _capturedRect.X ||
            clamped.Y != _capturedRect.Y ||
            clamped.Width != _capturedRect.Width ||
            clamped.Height != _capturedRect.Height;

        int refreshMs = Math.Max(200, _settings.BackdropRefreshMs);
        bool due = (DateTime.UtcNow - _lastCaptureUtc).TotalMilliseconds >= refreshMs;

        if (forceCapture || !_hasCapture || rectChanged || due)
        {
            // 画面没变时 Capture 返回 false，此时底图保持原样，不需要任何重绘
            bool changed = _capture.Capture(clamped);
            _lastCaptureUtc = DateTime.UtcNow;

            if (!_hasCapture || changed || BackdropImage.Source is null)
            {
                if (_capture.Bitmap is not null)
                {
                    BackdropImage.Source = _capture.Bitmap;
                    BackdropImage.Visibility = Visibility.Visible;
                    TintRect.Fill = (Brush)FindResource("GlassTint");
                    _hasCapture = true;
                }
            }

            _capturedRect = clamped;
        }

        RepositionBackdrop(windowRect, s);
    }

    /// <summary>底图内容不变，只把它摆到当前该在的位置（宿主局部坐标）。</summary>
    private void RepositionBackdrop(NativeMethods.RECT windowRect, double s)
    {
        if (!_hasCapture)
        {
            return;
        }

        double k = _scale <= 0 ? 1 : _scale;
        double origin = IslandMaxWidth / 2.0;
        double hostLeft = (Width - IslandMaxWidth) / 2.0;
        double hostTop = HostTopMargin;

        // 期望的落点与大小（未缩放的 host 局部坐标）
        double px = (_capturedRect.X - windowRect.Left) / s - hostLeft;
        double py = (_capturedRect.Y - windowRect.Top) / s - hostTop;

        if (Math.Abs(k - 1.0) < 0.001)
        {
            BackdropCounterScale.ScaleX = 1;
            BackdropCounterScale.ScaleY = 1;
            Canvas.SetLeft(BackdropImage, px);
            Canvas.SetTop(BackdropImage, py);
        }
        else
        {
            // Host 被整体放大了 k 倍（原点在顶部中心），这里反向补偿：
            // 底图仍然以 1:1 呈现，不会跟着岛一起被放大。
            BackdropCounterScale.ScaleX = 1.0 / k;
            BackdropCounterScale.ScaleY = 1.0 / k;
            Canvas.SetLeft(BackdropImage, origin + (px - origin) / k);
            Canvas.SetTop(BackdropImage, py / k);
        }

        BackdropImage.Width = _capturedRect.Width / s;
        BackdropImage.Height = _capturedRect.Height / s;
    }

    private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x2 <= x1 || y2 <= y1)
        {
            return new Int32Rect(0, 0, 0, 0);
        }
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Host 现在尺寸恒定，这里只保留兜底（正常情况下不会被调用）
    }

    // ==================================================================
    //  展开 / 收起
    // ==================================================================

    private void ApplyCollapsedGeometry()
    {
        // 清掉可能存在的动画，把状态摆回收起
        _glassClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        _glassClip.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
        _glassClip.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
        GlassBorder.BeginAnimation(Border.CornerRadiusProperty, null);

        _glassClip.Rect = CollapsedRect;
        _glassClip.RadiusX = CollapsedRadius;
        _glassClip.RadiusY = CollapsedRadius;
        GlassBorder.CornerRadius = new CornerRadius(CollapsedRadius);
    }

    /// <summary>
    /// 按设置决定显示哪些卡片，并据此算出岛的高度、窗口大小和整体缩放。
    /// 关掉音乐或消息后，那一行会真的收掉（不是留一块空白）。
    /// </summary>
    private void ApplyLayoutOptions()
    {
        bool music = _settings.EnableMusic;
        bool messages = _settings.EnableWindowsNotifications;

        MusicCard.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        MessagesCard.Visibility = messages ? Visibility.Visible : Visibility.Collapsed;

        double gap1 = music ? RowGap : 0;
        double musicH = music ? MusicHeight : 0;
        double gap2 = messages ? RowGap : 0;
        double messagesH = messages ? MessagesHeight : 0;

        Gap1Row.Height = new GridLength(gap1);
        MusicRow.Height = new GridLength(musicH);
        Gap2Row.Height = new GridLength(gap2);
        MessageRow.Height = new GridLength(messagesH);

        _islandHeight = PanelPadding + HeaderHeight + gap1 + musicH + gap2 + messagesH;

        // 自定义大小
        _scale = Math.Clamp(_settings.IslandScale, 0.7, 1.6);

        Host.Width = IslandMaxWidth;
        Host.Height = _islandHeight;
        Host.RenderTransformOrigin = new Point(0.5, 0);
        Host.RenderTransform = new ScaleTransform(_scale, _scale);

        // 窗口要能装下缩放后的岛
        Width = IslandMaxWidth * _scale + 16;
        Height = HostTopMargin + _islandHeight * _scale + 6;

        // 性能块
        PerfBlock.Visibility = _settings.ShowPerformance ? Visibility.Visible : Visibility.Collapsed;
        CollapsedCpu.Visibility = _settings.ShowPerformance && _settings.ShowPerformanceInPill
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 待办页签
        TodosTab.Visibility = _settings.ShowTodos ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.ShowTodos && _showingTodos)
        {
            SwitchView(showTodos: false);
        }

        ApplyCollapsedGeometry();
        ApplyPerformance();
    }

    /// <summary>刷新 CPU / 内存显示。</summary>
    private void ApplyPerformance()
    {
        if (!_settings.ShowPerformance)
        {
            return;
        }

        CpuText.Text = $"CPU {_monitor.CpuPercent:0}%";
        CpuText.Foreground = new SolidColorBrush(SystemMonitorService.LoadColor(_monitor.CpuPercent));

        MemText.Text = _settings.ShowMemoryDetail
            ? $"内存 {_monitor.MemoryPercent:0}%  {_monitor.UsedMemoryGb:0.0}/{_monitor.TotalMemoryGb:0.0}G"
            : $"内存 {_monitor.MemoryPercent:0}%";
        MemText.Foreground = new SolidColorBrush(SystemMonitorService.LoadColor(_monitor.MemoryPercent));

        if (CollapsedCpu.Visibility == Visibility.Visible)
        {
            CollapsedCpu.Text = $"CPU {_monitor.CpuPercent:0}%";
            CollapsedCpu.Foreground = new SolidColorBrush(SystemMonitorService.LoadColor(_monitor.CpuPercent));
        }
    }

    private static void OpenWeatherPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(WeatherUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private void OnHostMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        SetExpanded(true);
    }

    private void OnHostMouseLeave(object sender, MouseEventArgs e)
    {
        // 拖进度条 / 拖岛的时候不算离开
        if (_isSeeking || _isDragging)
        {
            return;
        }

        // 立刻收起，不等
        _collapseTimer.Stop();
        SetExpanded(false);
    }

    private void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
        {
            return;
        }

        _isExpanded = expanded;

        double toRadius = expanded ? ExpandedRadius : CollapsedRadius;
        Rect toRect = expanded ? ExpandedRect(_islandHeight) : CollapsedRect;

        var duration = TimeSpan.FromMilliseconds(expanded ? 420 : 340);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 动画期间不抓屏：抓一次十几毫秒，卡在动画里就会掉帧
        _isTransitioning = true;

        // 只动剪裁形状 + 圆角，完全不碰布局，所以每帧都很轻
        _glassClip.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(toRect, duration) { EasingFunction = ease });
        _glassClip.BeginAnimation(RectangleGeometry.RadiusXProperty,
            new DoubleAnimation(toRadius, duration) { EasingFunction = ease });
        _glassClip.BeginAnimation(RectangleGeometry.RadiusYProperty,
            new DoubleAnimation(toRadius, duration) { EasingFunction = ease });

        // Border.CornerRadius 没有可用的动画类型，就在渲染循环里逐帧跟着剪裁圆角走，
        // 这样描边和形状始终严丝合缝。
        CompositionTarget.Rendering -= OnTransitionFrame;
        CompositionTarget.Rendering += OnTransitionFrame;

        if (expanded)
        {
            // 展开内容从"缩在胶囊里"长出来
            ExpandedScale.ScaleX = 0.90;
            ExpandedScale.ScaleY = 0.90;
            ExpandedPanel.Visibility = Visibility.Visible;
            FadeTo(ExpandedPanel, 1, 240, 90);
            AnimateScale(ExpandedScale, 1.0, 380, 90,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 });

            // 胶囊内容缩小淡出，像被"吸"进去
            AnimateScale(CollapsedScale, 0.84, 170, 0);
            FadeTo(CollapsedPanel, 0, 150, 0, () => CollapsedPanel.Visibility = Visibility.Collapsed);
        }
        else
        {
            // 收起：展开内容整体缩放 + 淡出，缩回胶囊
            AnimateScale(ExpandedScale, 0.84, 260, 0,
                new CubicEase { EasingMode = EasingMode.EaseIn });
            FadeTo(ExpandedPanel, 0, 220, 40, () => ExpandedPanel.Visibility = Visibility.Collapsed);

            CollapsedPanel.Visibility = Visibility.Visible;
            CollapsedScale.ScaleX = 0.86;
            CollapsedScale.ScaleY = 0.86;
            FadeTo(CollapsedPanel, 1, 220, 120);
            AnimateScale(CollapsedScale, 1.0, 300, 120,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        }

        var settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = duration + TimeSpan.FromMilliseconds(90),
        };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            _isTransitioning = false;
            CompositionTarget.Rendering -= OnTransitionFrame;
            GlassBorder.CornerRadius = new CornerRadius(toRadius);
            UpdateEqualizerRunning();
            UpdateBackdrop(forceCapture: true);
        };
        settle.Start();
    }

    /// <summary>动画期间逐帧把描边圆角对齐到剪裁圆角。</summary>
    private void OnTransitionFrame(object? sender, EventArgs e)
    {
        double radius = Math.Max(0, _glassClip.RadiusX);
        var corner = new CornerRadius(radius);
        if (GlassBorder.CornerRadius != corner)
        {
            GlassBorder.CornerRadius = corner;
        }
    }

    private static void AnimateScale(ScaleTransform scale, double to, int milliseconds, int delayMs = 0, IEasingFunction? ease = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void FadeTo(UIElement element, double to, int milliseconds, int delayMs = 0, Action? onDone = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        if (onDone is not null)
        {
            animation.Completed += (_, _) =>
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
                onDone();
            };
        }

        element.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>来消息时玻璃轻轻"弹"一下。</summary>
    private void PopGlass(double amplitude = 0.985)
    {
        var scale = Glass.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform(1, 1);
            Glass.RenderTransform = scale;
        }

        // 缩放中心跟着当前状态的岛心走，不然收起时胶囊会"飘"
        Glass.RenderTransformOrigin = _isExpanded
            ? new Point(0.5, 0.35)
            : new Point(0.5, (CollapsedHeight / 2) / _islandHeight);

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(amplitude, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        });

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // ==================================================================
    //  时间 / 天气 / 音乐
    // ==================================================================

    private void UpdateClock()
    {
        var now = DateTime.Now;
        string time = now.ToString("HH:mm");
        CollapsedClock.Text = time;
        HeaderClock.Text = time;

        if (!_timer.IsActive)
        {
            HeaderSub.Text = $"{now.Month}月{now.Day}日 {WeekdayCn(now)}";
        }
    }

    private static string WeekdayCn(DateTime time) => time.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };

    private void ApplyWeather(WeatherInfo info)
    {
        var icon = WeatherIcons.Get(_weather.Kind);

        WeatherIcon.Source = icon;
        CollapsedWeatherIcon.Source = icon;

        WeatherTemp.Text = info.TemperatureText;
        CollapsedTemp.Text = info.TemperatureText;

        string city = string.IsNullOrWhiteSpace(info.City) ? string.Empty : info.City + " · ";
        WeatherDesc.Text = $"{city}{info.Condition} {info.RangeText}";
    }

    private void ApplyTrack(TrackInfo track)
    {
        if (!_settings.EnableMusic)
        {
            return;
        }

        bool hasTrack = track.HasTrack;

        TrackTitle.Text = hasTrack ? track.Title : "没有正在播放的音乐";

        bool hasArtist = hasTrack && !string.IsNullOrWhiteSpace(track.Artist);
        TrackArtist.Text = hasArtist ? track.Artist : string.Empty;
        TrackArtist.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        // 没有曲目时不显示来源标签
        TrackSourceChip.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        TrackSource.Text = hasTrack ? track.SourceName : string.Empty;

        var artwork = _music.Artwork;
        if (artwork is not null)
        {
            ArtworkBrush.ImageSource = artwork;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtworkBrush.ImageSource = null;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
        }

        PlayPauseGlyph.Text = track.IsPlaying ? "\uE769" : "\uE768";

        MiniEqualizer.Visibility = hasTrack && track.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        IdleDot.Visibility = hasTrack && track.IsPlaying ? Visibility.Collapsed : Visibility.Visible;

        UpdateEqualizerRunning();

        PrevButton.IsEnabled = hasTrack;
        NextButton.IsEnabled = hasTrack;
        PlayPauseButton.IsEnabled = hasTrack;
        ProgressHit.IsEnabled = hasTrack;

        ApplyProgress(track);
    }

    /// <summary>
    /// 均衡器只在收缩成小胶囊时跳：那时重绘面积小，几乎不花代价。
    /// 展开状态下它本来就看不见，一直跑只会白吃帧，动画就卡。
    /// </summary>
    private void UpdateEqualizerRunning()
    {
        var track = _music.Current;
        bool run = _settings.EnableMusic && track.HasTrack && track.IsPlaying && !_isExpanded;

        if (run)
        {
            _equalizer ??= BuildEqualizer();
            _equalizer.Begin();
        }
        else
        {
            _equalizer?.Stop();
        }
    }

    private void ApplyProgress(TrackInfo track)
    {
        if (_isSeeking)
        {
            return;
        }

        // 展开时才需要"滑"得好看；收起状态下进度条根本看不见，
        // 直接赋值就行，省掉一条每秒都在跑的动画。
        if (_isExpanded)
        {
            AnimateProgress(ProgressScale, track.Progress, 950);
        }
        else
        {
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = track.Progress;
        }

        TrackTime.Text = track.Duration > TimeSpan.Zero
            ? $"{Format(track.Position)} / {Format(track.Duration)}"
            : Format(track.Position);
    }

    /// <summary>把 ScaleX 平滑地推向目标值。</summary>
    private static void AnimateProgress(ScaleTransform scale, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            FillBehavior = FillBehavior.HoldEnd,
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";

    private Storyboard BuildEqualizer()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddBar(storyboard, Bar1Scale, 0.30, 1.00, 560);
        AddBar(storyboard, Bar2Scale, 0.95, 0.35, 420);
        AddBar(storyboard, Bar3Scale, 0.45, 0.90, 680);
        return storyboard;
    }

    private static void AddBar(Storyboard storyboard, ScaleTransform target, double from, double to, double ms)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(ScaleTransform.ScaleYProperty));
        storyboard.Children.Add(animation);
    }

    // ==================================================================
    //  进度条拖动
    // ==================================================================

    private void OnProgressMouseDown(object sender, MouseButtonEventArgs e)
    {
        var track = _music.Current;
        if (!track.HasTrack || track.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _isSeeking = true;
        _collapseTimer.Stop();
        ProgressHit.CaptureMouse();
        UpdateSeekPreview(e.GetPosition(ProgressHit));
        e.Handled = true;
    }

    private void OnProgressMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        UpdateSeekPreview(e.GetPosition(ProgressHit));
    }

    private async void OnProgressMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        ProgressHit.ReleaseMouseCapture();

        var track = _music.Current;
        if (track.Duration > TimeSpan.Zero)
        {
            double ratio = RatioFromPoint(e.GetPosition(ProgressHit));
            await _music.SeekAsync(TimeSpan.FromSeconds(ratio * track.Duration.TotalSeconds));
        }

        ApplyProgress(_music.Current);
        MaybeStartCollapseTimer();
    }

    private void UpdateSeekPreview(Point point)
    {
        var track = _music.Current;
        if (track.Duration <= TimeSpan.Zero)
        {
            return;
        }

        double ratio = RatioFromPoint(point);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = ratio;

        var preview = TimeSpan.FromSeconds(ratio * track.Duration.TotalSeconds);
        TrackTime.Text = $"{Format(preview)} / {Format(track.Duration)}";
    }

    private double RatioFromPoint(Point point)
    {
        double width = ProgressHit.ActualWidth;
        return width <= 0 ? 0 : Math.Clamp(point.X / width, 0, 1);
    }

    // ==================================================================
    //  消息
    // ==================================================================

    private void HandleIncoming(IslandMessage message)
    {
        if (_closing)
        {
            return;
        }

        // 本地推送进来的消息也放进列表
        if (!message.FromWindowsNotifications)
        {
            _recent.Insert(0, message);
            while (_recent.Count > 15)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
            RebuildMessageList(_notifications.Current);
        }

        SetExpanded(true);
        PopGlass();
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    /// <summary>合并 Windows 未读通知 + 本地推送，重建列表。岛的大小始终不变。</summary>
    private void RebuildMessageList(IReadOnlyList<IslandMessage> windowsNotifications)
    {
        var merged = new List<IslandMessage>(windowsNotifications.Count + _recent.Count);

        foreach (var message in windowsNotifications)
        {
            if (!_dismissed.Contains(message.Key))
            {
                merged.Add(message);
            }
        }

        foreach (var message in _recent)
        {
            if (!_dismissed.Contains(message.Key))
            {
                merged.Add(message);
            }
        }

        merged.Sort((a, b) => b.ReceivedAt.CompareTo(a.ReceivedAt));

        MessageList.ItemsSource = merged;

        // 空状态和计数都交给 RefreshTodoView 统一决定，避免两边打架
        RefreshTodoView();
        UpdateMoreHint();
    }

    /// <summary>列表重建后刷新一次提示。</summary>
    private void UpdateMoreHint()
        => UpdateMoreHint(MessageScroll.ExtentHeight, MessageScroll.ViewportHeight, MessageScroll.VerticalOffset);

    /// <summary>
    /// 用 ScrollChanged 事件自己带的数据判断，而不是去读 ViewportHeight/ExtentHeight——
    /// 在滚动事件里读这两个属性会强制再跑一次布局，可能把自己再次触发，形成死循环（表现就是 CPU 满载、点不动）。
    /// </summary>
    private void UpdateMoreHint(double extent, double viewport, double offset)
    {
        // 待办视图下不需要"看更多"提示
        if (_showingTodos)
        {
            MoreHintText.Text = string.Empty;
            return;
        }

        if (MessageList.ItemsSource is not System.Collections.ICollection items || items.Count == 0)
        {
            MoreHintText.Text = string.Empty;
            return;
        }

        if (extent <= viewport + 1)
        {
            MoreHintText.Text = string.Empty;
            return;
        }

        bool atBottom = offset >= extent - viewport - 1;
        MoreHintText.Text = atBottom ? "滚轮往回看 ↑" : "滚轮看更多 ↓";
    }

    /// <summary>点某条消息：能打开目标就打开，否则把对应窗口拉到前面。</summary>
    private void OnMessageListClick(object sender, MouseButtonEventArgs e)
    {
        var message = FindMessage(e.OriginalSource as DependencyObject);
        if (message is null)
        {
            return;
        }

        if (message.FromWindowsNotifications && !string.IsNullOrWhiteSpace(message.LaunchTarget))
        {
            if (TryOpenTarget(message.LaunchTarget))
            {
                return;
            }
        }

        FocusTargetWindow();
    }

    /// <summary>✕：把这条从岛上移除。</summary>
    private void OnDismissMessage(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button { Tag: IslandMessage message })
        {
            return;
        }

        _dismissed.Add(message.Key);
        RebuildMessageList(_notifications.Current);
    }

    private static IslandMessage? FindMessage(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is IslandMessage message)
            {
                return message;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool TryOpenTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string url = target.Trim();
        int hash = url.IndexOf('#');
        if (hash > 0)
        {
            url = url[..hash];
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void FocusTargetWindow()
    {
        if (_actionTargetWindow != IntPtr.Zero)
        {
            QqWatcher.FocusWindow(_actionTargetWindow);
        }
    }

    private void MaybeStartCollapseTimer()
    {
        if (_contextMenuOpen || Host.IsMouseOver)
        {
            return;
        }

        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    /// <summary>把天气块排除在拖动之外（点它要开网页）。</summary>
    private bool IsWeatherBlock(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (ReferenceEquals(current, WeatherBlock))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    // ==================================================================
    //  待办
    // ==================================================================

    private void OnShowMessages(object sender, RoutedEventArgs e) => SwitchView(showTodos: false);

    private void OnShowTodos(object sender, RoutedEventArgs e) => SwitchView(showTodos: true);

    private void SwitchView(bool showTodos)
    {
        _showingTodos = showTodos;

        MessageScroll.Visibility = showTodos ? Visibility.Collapsed : Visibility.Visible;
        TodoScroll.Visibility = showTodos ? Visibility.Visible : Visibility.Collapsed;
        AddTodoButton.Visibility = showTodos ? Visibility.Visible : Visibility.Collapsed;

        RefreshTodoView();
        UpdateMoreHint();

        // 选中态
        if (showTodos)
        {
            TodosTab.Foreground = Brushes.White;
            TodosTab.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            MessagesTab.Foreground = (Brush)FindResource("TextTertiary");
            MessagesTab.Background = Brushes.Transparent;
        }
        else
        {
            MessagesTab.Foreground = Brushes.White;
            MessagesTab.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            TodosTab.Foreground = (Brush)FindResource("TextTertiary");
            TodosTab.Background = Brushes.Transparent;
        }
    }

    /// <summary>刷新待办区域的显示（数量、空状态、页签文案）。</summary>
    private void RefreshTodoView()
    {
        int active = _todos.ActiveCount;
        int total = _todos.Items.Count;

        // 空状态必须跟着页签走：待办页只显示待办提示，消息页只显示消息提示，
        // 否则两边的提示会叠在一起，看着像"合在一起了"。
        TodoEmpty.Visibility = _showingTodos && total == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        int messageCount = MessageList.ItemsSource is System.Collections.ICollection c ? c.Count : 0;
        MessageEmpty.Visibility = !_showingTodos && messageCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        TodosTab.Content = active > 0 ? $"待办 {active}" : "待办";

        if (_showingTodos)
        {
            MessageCountText.Text = total > 0 ? $"共 {total} 项 · {active} 项未完成" : string.Empty;
        }
        else
        {
            MessageCountText.Text = messageCount > 0 ? $"共 {messageCount} 条未读" : string.Empty;
        }
    }

    private void OnAddTodo(object sender, RoutedEventArgs e)
    {
        var dialog = new InputWindow("添加待办", "要做什么？");
        dialog.Confirmed = text =>
        {
            _todos.Add(text);
            SwitchView(showTodos: true);
            SetExpanded(true);
        };
        dialog.Show();
        dialog.Activate();
    }

    private void OnToggleTodo(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button { Tag: TodoItem item })
        {
            _todos.Toggle(item);
        }
    }

    private void OnDeleteTodo(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button { Tag: TodoItem item })
        {
            _todos.Remove(item);
        }
    }

    // ==================================================================
    //  倒计时 / 闹钟
    // ==================================================================

    private void ApplyTimerState()
    {
        bool active = _timer.IsActive;

        TimerBig.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        TimerTrack.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTimerGroup.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        // 计时中：把时钟缩小，让倒计时成为主角
        HeaderClock.FontSize = active ? 15 : 34;
        HeaderClock.LineHeight = active ? 18 : 36;

        if (active)
        {
            string text = TimerService.Format(_timer.Remaining);
            TimerBig.Text = text;
            CollapsedTimer.Text = text;

            HeaderSub.Text = _timer.Kind == IslandTimerKind.Countdown
                ? $"倒计时中 · 剩 {TimerService.Format(_timer.Remaining)}"
                : $"闹钟 · {_timer.Label}";

            // 200ms 一跳，进度条滑着走
            AnimateProgress(TimerTrackScale, _timer.Progress, 205);
        }
        else
        {
            var now = DateTime.Now;
            HeaderSub.Text = $"{now.Month}月{now.Day}日 {WeekdayCn(now)}";
        }
    }

    private void OnTimerFinished()
    {
        // 时间到：展开、闪一下、弹一下，并把时钟位置换成提示
        _timerResetTimer.Stop();
        _timerResetTimer.Start();

        TimerBig.Visibility = Visibility.Visible;
        TimerBig.Text = "00:00";
        CollapsedTimerGroup.Visibility = Visibility.Visible;
        CollapsedTimer.Text = "时间到";
        TimerTrack.Visibility = Visibility.Collapsed;
        HeaderClock.FontSize = 15;
        HeaderClock.LineHeight = 18;
        HeaderSub.Text = _timer.Kind == IslandTimerKind.Countdown ? "倒计时结束" : "闹钟时间到";

        SetExpanded(true);
        PlayFinishFlash();
        PlayFinishBounce();

        try
        {
            NativeMethods.MessageBeep(0x00000030); // MB_ICONEXCLAMATION
        }
        catch
        {
            // 没声音也无所谓
        }
    }

    /// <summary>金色闪光扫一下。</summary>
    private void PlayFinishFlash()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });

        animation.RepeatBehavior = new RepeatBehavior(1);
        TimerFlash.BeginAnimation(OpacityProperty, animation);
        TimerFlash.Opacity = 0;
    }

    /// <summary>岛本身跳两下。</summary>
    private void PlayFinishBounce()
    {
        var scale = Glass.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform(1, 1);
            Glass.RenderTransformOrigin = new Point(0.5, 0.35);
            Glass.RenderTransform = scale;
        }

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(660))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(980)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        });

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void OpenTimerDialog()
    {
        var dialog = new TimerWindow { Owner = null };

        dialog.Confirmed = (kind, duration, alarmMinutes) =>
        {
            if (kind == IslandTimerKind.Countdown)
            {
                _timer.StartCountdown(duration);
            }
            else
            {
                var now = DateTime.Now;
                int hour = alarmMinutes / 60;
                int minute = alarmMinutes % 60;
                var target = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
                _timer.StartAlarm(new DateTimeOffset(target), $"{hour:00}:{minute:00}");
            }

            ApplyTimerState();
            SetExpanded(true);
        };

        dialog.Show();
        dialog.Activate();
    }

    // ==================================================================
    //  右键菜单 / 设置
    // ==================================================================

    private void BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1F, 0x24)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };

        menu.Opened += (_, _) => _contextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
            MaybeStartCollapseTimer();
        };

        menu.Items.Add(MenuItem("倒计时 / 闹钟…", OpenTimerDialog));
        menu.Items.Add(MenuItem("停止计时", () =>
        {
            _timer.Stop();
            ApplyTimerState();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("设置…", OpenSettings));
        menu.Items.Add(MenuItem("刷新天气", async () => await _weather.RefreshAsync()));
        menu.Items.Add(MenuItem("打开配置文件", OpenConfigFile));
        menu.Items.Add(MenuItem("推送接口说明", OpenReadme));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("退出", Close));

        Host.ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6, 10, 6),
        };
        item.Click += (_, _) => action();
        return item;
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        Diagnostics.Write("OpenSettings: 进入");

        if (_settingsWindow is not null)
        {
            Diagnostics.Write("OpenSettings: 已经开着，激活");
            _settingsWindow.Activate();
            return;
        }

        try
        {
            _settingsWindow = new SettingsWindow(_settings);
            Diagnostics.Write("OpenSettings: 窗口构造成功");

            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ApplyAppearance();
                RebuildMessageList(_notifications.Current);
            };

            _settingsWindow.Show();
            Diagnostics.Write("OpenSettings: Show 完成，IsVisible=" + _settingsWindow.IsVisible);
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("OpenSettings 失败: " + ex);
            _settingsWindow = null;
        }
    }

    private void ApplyAppearance()
    {
        _capture.BlurRadius = Math.Clamp((int)Math.Round(_settings.GlassBlurRadius / 8.0), 1, 8);
        TintRect.Opacity = Math.Clamp(_settings.GlassTintOpacity, 0, 1);
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(600, _settings.MessageHoldMs));

        // 显示内容 / 缩放变了都要重排
        ApplyLayoutOptions();
        PositionWindow();
        UpdateBackdrop(forceCapture: true);
    }

    private void OpenConfigFile()
    {
        try
        {
            _settings.Save();
            Process.Start(new ProcessStartInfo(AppSettings.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private void OpenReadme()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "推送接口说明.txt");
            File.WriteAllText(path, ReadmeText(), System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private string ReadmeText() => $$"""
        DynamicIsland 本地推送接口
        ==========================

        端口：{{_settings.PushPort}}（在设置里改，0 = 关闭）
        只监听 127.0.0.1，外部网络访问不到。
        支持 GET / POST，请求体可以是 JSON，也可以是纯文本。

        1) 推一条消息（PowerShell）
           Invoke-RestMethod -Uri http://127.0.0.1:{{_settings.PushPort}}/message -Method Post `
             -Body '{"source":"QQ","title":"张三","text":"在吗"}'

        2) 纯文本也可以，会当成消息正文
           Invoke-RestMethod -Uri http://127.0.0.1:{{_settings.PushPort}}/message -Method Post `
             -Body "构建完成"

        3) 用 curl 的话
           curl -X POST http://127.0.0.1:{{_settings.PushPort}}/message -d "构建完成"

        4) 探活
           Invoke-RestMethod -Uri http://127.0.0.1:{{_settings.PushPort}}/ping

        中文没事：UTF-8 和 GB2312 的请求体都能正确解码。
        """;
}
