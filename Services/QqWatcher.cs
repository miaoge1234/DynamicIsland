using System.Text.RegularExpressions;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// 监听电脑上 QQ 的来消息 / 来电话。
///
/// 两个信号来源，互补：
///  1) Shell 钩子的窗口闪烁通知（HSHELL_FLASH）——QQ 收到消息或来电时会让窗口
///     请求注意，这个信号最可靠，聊天窗口没打开也能收到；
///  2) 轮询 QQ 窗口标题——旧版 QQ 会把未读数写在标题里（如 "张三(3)"），
///     顺带拿到联系人 / 群名；标题里出现"语音通话/视频通话"就判定为来电。
///
/// 注意：能拿到的是"谁发来的"，拿不到消息正文（QQ 不把正文暴露在窗口标题里）。
/// 需要正文可以用本地推送接口，见 PushServer。
/// </summary>
public sealed class QqWatcher : IDisposable
{
    private static readonly Regex UnreadInBracket = new(
        @"[\(（\[【]\s*(\d+)\s*[\)）\]】]",
        RegexOptions.Compiled);

    private static readonly Regex UnreadInText = new(
        @"(\d+)\s*条(?:新)?消息",
        RegexOptions.Compiled);

    private static readonly string[] CallKeywords =
    {
        "语音通话", "视频通话", "语音聊天", "视频聊天",
        "通话中", "正在通话", "等待接听", "等待对方接听", "来电",
    };

    /// <summary>这些标题不是聊天窗口，忽略掉，避免误报。</summary>
    private static readonly string[] IgnoredTitles =
    {
        "qq", "qqnt", "tim", "登录", "设置", "消息管理器", "安全", "更新",
        "正在登录", "腾讯", "扫一扫", "截图",
    };

    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _poll;
    private readonly Dictionary<IntPtr, WatchedWindow> _known = new();
    private readonly Dictionary<IntPtr, DateTimeOffset> _recentFlash = new();

    private bool _started;

    public QqWatcher(Dispatcher dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>检测到 QQ 事件（新消息 / 来电 / 通话结束）。</summary>
    public event EventHandler<IslandMessage>? Detected;

    public void Start()
    {
        if (_started || !_settings.EnableQqWatch)
        {
            return;
        }

        _started = true;
        Poll();
        _poll.Start();
    }

    /// <summary>由 Shell 钩子调用：某个窗口请求注意（闪烁）。</summary>
    public void NotifyFlash(IntPtr hwnd)
    {
        if (!_started || hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsQqProcess(WindowEnumerator.GetProcessName(pid)))
            {
                return;
            }

            // 同一个窗口短时间内的多次闪烁合并成一条
            var now = DateTimeOffset.Now;
            if (_recentFlash.TryGetValue(hwnd, out var last) &&
                (now - last) < TimeSpan.FromSeconds(4))
            {
                return;
            }
            _recentFlash[hwnd] = now;
            if (_recentFlash.Count > 128)
            {
                _recentFlash.Clear();
            }

            string title = WindowEnumerator.GetTitle(hwnd);
            string contact = ExtractContactName(title);

            // 来电不再特殊处理，和普通消息一样进列表
            Raise(new IslandMessage
            {
                Kind = MessageKind.Message,
                Source = "QQ",
                AppId = "QQ",
                Title = contact.Length > 0 ? contact : "QQ",
                Text = "有新动态",
                TargetWindow = hwnd,
            });
        }
        catch
        {
            // 忽略
        }
    }

    private void Poll()
    {
        try
        {
            var current = new Dictionary<IntPtr, WatchedWindow>();

            foreach (var window in WindowEnumerator.EnumerateVisible())
            {
                if (!IsQqProcess(window.ProcessName))
                {
                    continue;
                }

                string title = window.Title.Trim();
                if (title.Length == 0 || IsIgnoredTitle(title))
                {
                    continue;
                }

                bool isCall = IsCallTitle(title);
                int unread = isCall ? 0 : ExtractUnread(title);

                current[window.Handle] = new WatchedWindow(title, unread, isCall, window.Handle);
                if (!_known.TryGetValue(window.Handle, out var previous))
                {
                    // 新出现的窗口：只有带未读数才算新消息
                    if (unread > 0)
                    {
                        Raise(new IslandMessage
                        {
                            Kind = MessageKind.Message,
                            Source = "QQ",
                            AppId = "QQ",
                            Title = ExtractContactName(title),
                            Text = $"有 {unread} 条新消息",
                            TargetWindow = window.Handle,
                        });
                    }
                    continue;
                }

                // 未读数上升 = 新消息（标题没变但计数涨了也算）
                if (unread > previous.Unread)
                {
                    Raise(new IslandMessage
                    {
                        Kind = MessageKind.Message,
                        Source = "QQ",
                        AppId = "QQ",
                        Title = ExtractContactName(title),
                        Text = unread > 1 ? $"有 {unread} 条新消息" : "发来一条新消息",
                        TargetWindow = window.Handle,
                    });
                }
            }

            _known.Clear();
            foreach (var pair in current)
            {
                _known[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // 忽略
        }
    }

    private void Raise(IslandMessage message) => _dispatcher.BeginInvoke(() => Detected?.Invoke(this, message));

    /// <summary>把 QQ 窗口标题里的联系人 / 群名抠出来。</summary>
    public static string ExtractContactName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "QQ";
        }

        string name = UnreadInBracket.Replace(title, string.Empty);
        name = UnreadInText.Replace(name, string.Empty);
        name = name.Replace("新消息", string.Empty).Trim(' ', '-', '·', '(', ')', '[', ']', '（', '）');
        return name.Length == 0 ? "QQ" : name;
    }

    private static int ExtractUnread(string title)
    {
        var m = UnreadInBracket.Match(title);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n1))
        {
            return n1;
        }

        m = UnreadInText.Match(title);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n2))
        {
            return n2;
        }

        return 0;
    }

    private static bool IsCallTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        foreach (string keyword in CallKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIgnoredTitle(string title)
    {
        string t = title.Trim();
        foreach (string ignored in IgnoredTitles)
        {
            if (t.Equals(ignored, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQqProcess(string processName) =>
        processName.Equals("QQ", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("QQNT", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("TIM", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 QQ 窗口拉到前台（点岛上消息卡片时用）。</summary>
    public static void FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose()
    {
        _poll.Stop();
    }

    private sealed record WatchedWindow(string Title, int Unread, bool IsCall, IntPtr Handle);
}
