using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Services;

/// <summary>
/// 配置。存在 %APPDATA%\DynamicIsland\settings.json，改动后重启生效。
/// 岛上的右键菜单可以直接打开这个文件。
/// </summary>
public sealed class AppSettings
{
    /// <summary>天气城市。填了就用手填的，留空才按 IP 自动定位</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>是否允许按公网 IP 自动定位城市</summary>
    public bool AutoLocate { get; set; } = true;

    /// <summary>屏幕顶部再往下偏移多少像素（没拖动过时生效）</summary>
    public double TopOffset { get; set; } = 10;

    /// <summary>拖动后记住的位置（DIP，相对屏幕左上角）。null = 顶部居中</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    /// <summary>玻璃模糊强度（越小越"淡"）</summary>
    public double GlassBlurRadius { get; set; } = 22;

    /// <summary>
    /// 是否让系统截图/录屏拍不到这个岛。
    /// 开着可以避免背景抓屏把自己的画面反复模糊；
    /// 如果你要录屏展示这个岛，把它设成 false。
    /// </summary>
    public bool ExcludeFromCapture { get; set; } = true;

    /// <summary>玻璃白染色不透明度 0~1，越小越通透</summary>
    public double GlassTintOpacity { get; set; } = 1.0;

    /// <summary>
    /// 背景模糊的兜底刷新间隔（毫秒）。
    /// 平时不靠它：切换前台窗口、展开/收起时都会自动重抓，
    /// 这里只是防止画面变了没人通知（比如换了壁纸）。
    /// 设成 0 就完全关闭兜底轮询，最省电。
    /// </summary>
    public int BackdropRefreshMs { get; set; } = 2500;

    /// <summary>收到新消息后岛保持展开多久（毫秒）。鼠标离开是立刻收起的。</summary>
    public int MessageHoldMs { get; set; } = 2600;

    // ---------------- 显示内容 ----------------

    /// <summary>岛整体缩放（0.8 ~ 1.5，1 = 默认大小）</summary>
    public double IslandScale { get; set; } = 1.0;

    /// <summary>显示 CPU / 内存占用</summary>
    public bool ShowPerformance { get; set; } = true;

    /// <summary>CPU / 内存这几行里显示内存详情（已用 / 总量）</summary>
    public bool ShowMemoryDetail { get; set; } = true;

    /// <summary>收缩成小胶囊时也显示 CPU 占用</summary>
    public bool ShowPerformanceInPill { get; set; }

    /// <summary>显示待办页签</summary>
    public bool ShowTodos { get; set; } = true;

    /// <summary>开机自启</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>本地推送接口端口，0 = 关闭</summary>
    public int PushPort { get; set; } = 7788;

    public bool EnableMusic { get; set; } = true;
    public bool EnableWeather { get; set; } = true;
    public bool EnableQqWatch { get; set; } = true;

    /// <summary>显示 Windows 通知中心里的未读消息（岛底部那栏）</summary>
    public bool EnableWindowsNotifications { get; set; } = true;

    /// <summary>天气刷新间隔（分钟）。默认 1 小时。</summary>
    public int WeatherRefreshMinutes { get; set; } = 60;

    // ---------------- 载入 / 保存 ----------------

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，不要让程序起不来
        }

        var fresh = new AppSettings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);

            // 带 BOM 写：Windows PowerShell 5.1 这些老工具默认按本地代码页读文件，
            // 不带 BOM 的话中文会被读成乱码（比如"福州"变成"绂忓窞"）。
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions),
                new System.Text.UTF8Encoding(true));
        }
        catch
        {
            // 忽略
        }
    }
}
