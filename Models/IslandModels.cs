namespace DynamicIsland.Models;

public enum MessageKind
{
    /// <summary>普通消息（QQ 聊天、本地推送等）</summary>
    Message,

    /// <summary>QQ 语音/视频通话来电</summary>
    IncomingCall,

    /// <summary>通话结束</summary>
    CallEnded,
}

/// <summary>岛上展示的一条消息。</summary>
public sealed class IslandMessage
{
    /// <summary>稳定标识，用来判断"这条还在不在"（Windows 通知被清掉后就没了）</summary>
    public string Key { get; init; } = Guid.NewGuid().ToString("N");

    public required MessageKind Kind { get; init; }

    /// <summary>来源显示名，例如 "QQ"、"微信"</summary>
    public string Source { get; init; } = "QQ";

    /// <summary>来源的原始标识（AUMID / 进程名），过滤时用</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>联系人 / 群名 / 通知标题</summary>
    public string Title { get; init; } = "新消息";

    /// <summary>正文，拿不到就留空，UI 会显示占位文案</summary>
    public string Text { get; init; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>可点击跳转的目标窗口（QQ 聊天/通话窗口）</summary>
    public IntPtr TargetWindow { get; init; }

    /// <summary>点击时要打开的 URL / 协议（来自通知的 launch 属性）</summary>
    public string LaunchTarget { get; init; } = string.Empty;

    /// <summary>这条消息是不是来自 Windows 通知中心</summary>
    public bool FromWindowsNotifications { get; init; }

    public bool HasBody => !string.IsNullOrWhiteSpace(Text);

    /// <summary>列表里标题最多显示多少个字，超了用 …… </summary>
    private const int TitleLimit = 22;

    /// <summary>列表里正文最多显示多少个字，超了用 ……（完整内容在悬停提示里）</summary>
    private const int TextLimit = 46;

    /// <summary>列表里显示的标题（截断）</summary>
    public string DisplayTitle => Truncate(string.IsNullOrWhiteSpace(Title) ? Source : Title, TitleLimit);

    /// <summary>列表里显示的正文（截断）</summary>
    public string DisplayText => Truncate(Text, TextLimit);

    /// <summary>悬停提示里的完整标题</summary>
    public string TooltipTitle => string.IsNullOrWhiteSpace(Title) ? Source : Title;

    /// <summary>悬停提示里的完整正文</summary>
    public string TooltipBody => string.IsNullOrWhiteSpace(Text) ? "（这条通知没有正文）" : Text;

    /// <summary>悬停提示最下面一行：来源 + 时间</summary>
    public string SourceAndTime =>
        $"{Source} · {ReceivedAt.ToLocalTime():HH:mm}";

    private static string Truncate(string value, int limit)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // 正文里的换行在列表里会挤成一行，先压平
        string flat = value.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        while (flat.Contains("  "))
        {
            flat = flat.Replace("  ", " ");
        }

        return flat.Length <= limit ? flat : flat[..limit] + "……";
    }

    /// <summary>徽标里显示的字（最多两个字）</summary>
    public string BadgeText
    {
        get
        {
            string s = string.IsNullOrWhiteSpace(Source) ? "?" : Source.Trim();
            return s.Length <= 2 ? s : s[..2];
        }
    }

    /// <summary>相对时间，比如 "刚刚"、"3 分钟前"</summary>
    public string TimeText
    {
        get
        {
            var delta = DateTimeOffset.Now - ReceivedAt;
            if (delta.TotalSeconds < 45)
            {
                return "刚刚";
            }
            if (delta.TotalMinutes < 60)
            {
                return $"{(int)delta.TotalMinutes} 分钟";
            }
            if (delta.TotalHours < 24)
            {
                return $"{(int)delta.TotalHours} 小时";
            }
            return ReceivedAt.ToLocalTime().ToString("M/d");
        }
    }

    /// <summary>徽标底色，按来源区分</summary>
    public System.Windows.Media.Brush BadgeBrush
    {
        get
        {
            System.Windows.Media.Color color =
                Kind is MessageKind.IncomingCall or MessageKind.CallEnded
                    ? System.Windows.Media.Color.FromRgb(0x2E, 0xA8, 0x4A)
                    : Source switch
                    {
                        "QQ" => System.Windows.Media.Color.FromRgb(0x2C, 0x7C, 0xE0),
                        "微信" => System.Windows.Media.Color.FromRgb(0x2A, 0xAE, 0x62),
                        "钉钉" => System.Windows.Media.Color.FromRgb(0x1E, 0x8C, 0xF0),
                        "推送" => System.Windows.Media.Color.FromRgb(0x7C, 0x5C, 0xE0),
                        "Windows 安全" => System.Windows.Media.Color.FromRgb(0xC0, 0x50, 0x4D),
                        _ => System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80),
                    };

            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}

/// <summary>当前播放的音乐。</summary>
public sealed class TrackInfo
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;

    /// <summary>源程序标识，例如 cloudmusic.exe</summary>
    public string SourceAppId { get; init; } = string.Empty;

    /// <summary>源程序中文名，例如 网易云音乐</summary>
    public string SourceName { get; init; } = string.Empty;

    public bool IsPlaying { get; init; }

    /// <summary>是否来自回退方案（读窗口标题）而非系统媒体控制</summary>
    public bool IsFallbackSource { get; init; }

    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    public double Progress => Duration > TimeSpan.Zero
        ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;
}

/// <summary>天气。</summary>
public sealed class WeatherInfo
{
    public string City { get; init; } = string.Empty;
    public double Temperature { get; init; }
    public double FeelsLike { get; init; }
    public double TempMax { get; init; }
    public double TempMin { get; init; }
    public int Humidity { get; init; }
    public string Condition { get; init; } = string.Empty;

    /// <summary>一个字符的天气图标（emoji 或符号）</summary>
    public string Glyph { get; init; } = "☁";

    public bool IsDay { get; init; } = true;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public string TemperatureText => $"{Math.Round(Temperature)}°";
    public string RangeText => $"{Math.Round(TempMin)}° / {Math.Round(TempMax)}°";
}
