using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using DynamicIsland.Models;
using Microsoft.Data.Sqlite;

namespace DynamicIsland.Services;

/// <summary>
/// 读 Windows 通知中心里"当前还没清掉的通知"，当作未读消息显示在岛上。
///
/// 做法：把 wpndatabase.db 连同 -wal/-shm 复制到临时目录（不能直接开系统的，
/// WpnService 占着），再用 SQLite 只读查询：
///    Notification ←→ NotificationHandler，Payload 里就是 toast 的 XML。
/// 在通知中心把消息划掉后，对应的行会被删掉，所以这个列表天然就是"未读"。
///
/// 注意：只读，绝不写系统文件。
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const string DbName = "wpndatabase.db";

    /// <summary>这些来源默认不显示（系统小组件之类的纯噪音）</summary>
    private static readonly string[] DefaultNoise =
    {
        "WebExperience_cw5n1h2txyewy!Widgets",
        "Windows.FilePicker",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "consentcoordinator",
        "AppResolverUX",
    };

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly string _workDir;

    private DateTime _lastDbStamp = DateTime.MinValue;
    private DateTime _lastWalStamp = DateTime.MinValue;
    private long _lastDbLength = -1;
    private long _lastWalLength = -1;

    private Dictionary<string, IslandMessage> _snapshot = new();
    private bool _broken;

    /// <summary>正在后台读，别叠加第二次</summary>
    private volatile bool _reading;

    /// <summary>上一次的内容指纹，内容没变就不惊动界面</summary>
    private string _lastSignature = string.Empty;

    public NotificationService(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _workDir = Path.Combine(Path.GetTempPath(), "DynamicIsland", "wpn");

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>当前完整的未读通知列表（新的在前）。</summary>
    public IReadOnlyList<IslandMessage> Current { get; private set; } = Array.Empty<IslandMessage>();

    /// <summary>整个列表变了（新增或有人清掉了通知）。</summary>
    public event EventHandler<IReadOnlyList<IslandMessage>>? Changed;

    /// <summary>只在新通知进来时触发（用来弹一下岛）。</summary>
    public event EventHandler<IslandMessage>? Added;

    /// <summary>见过的所有来源，给设置界面用来做过滤。</summary>
    public Dictionary<string, string> KnownApps { get; } = new();

    public string? LastError { get; private set; }

    private static string SourceDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications");

    public void Start()
    {
        if (!_settings.EnableWindowsNotifications)
        {
            return;
        }

        Poll();
        _timer.Start();
    }

    private void Poll()
    {
        if (_broken || _reading || !_settings.EnableWindowsNotifications)
        {
            return;
        }

        try
        {
            string db = Path.Combine(SourceDir, DbName);
            if (!File.Exists(db))
            {
                LastError = "找不到通知数据库";
                return;
            }

            // 数据库或 WAL 变了才需要重新读。这一步只是看文件属性，很便宜。
            var dbInfo = new FileInfo(db);
            string wal = db + "-wal";
            bool hasWal = File.Exists(wal);
            var walInfo = hasWal ? new FileInfo(wal) : null;

            bool changed =
                dbInfo.LastWriteTimeUtc != _lastDbStamp ||
                dbInfo.Length != _lastDbLength ||
                (walInfo is not null &&
                 (walInfo.LastWriteTimeUtc != _lastWalStamp || walInfo.Length != _lastWalLength));

            if (!changed)
            {
                return;
            }

            _lastDbStamp = dbInfo.LastWriteTimeUtc;
            _lastDbLength = dbInfo.Length;
            if (walInfo is not null)
            {
                _lastWalStamp = walInfo.LastWriteTimeUtc;
                _lastWalLength = walInfo.Length;
            }

            // 复制数据库（几 MB）+ 打开 SQLite + 解析几百条 XML 都要几十上百毫秒，
            // 放在 UI 线程上就会把动画和点击卡住，所以整个丢到后台去。
            _reading = true;
            Task.Run(() =>
            {
                try
                {
                    var result = ReadNotifications();
                    if (result is null)
                    {
                        return;
                    }

                    _dispatcher.BeginInvoke(() => ApplySnapshot(result.Value.Messages, result.Value.Apps));
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
                finally
                {
                    _reading = false;
                }
            });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _broken = true;
        }
    }

    /// <summary>回到 UI 线程更新状态。内容真的变了才通知界面，避免无意义的刷新。</summary>
    private void ApplySnapshot(Dictionary<string, IslandMessage> fresh, Dictionary<string, string> apps)
    {
        foreach (var pair in apps)
        {
            KnownApps[pair.Key] = pair.Value;
        }

        // 指纹：键 + 可见文字。时间戳变化不会影响它，所以"没新消息"时不会重刷列表。
        var builder = new System.Text.StringBuilder();
        foreach (var message in fresh.Values.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            builder.Append(message.Key).Append('\u0001')
                   .Append(message.Title).Append('\u0001')
                   .Append(message.Text).Append('\u0002');
        }

        string signature = builder.ToString();
        bool contentChanged = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
        _lastSignature = signature;

        var previousKeys = new HashSet<string>(_snapshot.Keys, StringComparer.Ordinal);

        _snapshot = fresh;
        Current = fresh.Values.OrderByDescending(m => m.ReceivedAt).ToList();

        if (!contentChanged)
        {
            return;
        }

        Changed?.Invoke(this, Current);

        foreach (var message in Current
                     .Where(m => !previousKeys.Contains(m.Key))
                     .OrderBy(m => m.ReceivedAt))
        {
            Added?.Invoke(this, message);
        }
    }

    private (Dictionary<string, IslandMessage> Messages, Dictionary<string, string> Apps)? ReadNotifications()
    {
        Directory.CreateDirectory(_workDir);

        // 三个文件要一起复制，否则 WAL 里的新通知读不到
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string from = Path.Combine(SourceDir, DbName + suffix);
            if (!File.Exists(from))
            {
                continue;
            }

            try
            {
                File.Copy(from, Path.Combine(_workDir, DbName + suffix), true);
            }
            catch
            {
                // 单个文件复制失败就跳过
            }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_workDir, DbName),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        };

        var result = new Dictionary<string, IslandMessage>(StringComparer.Ordinal);
        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.Id, n.ArrivalTime, n.Payload, h.PrimaryId
            FROM Notification n
            LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId
            ORDER BY n.ArrivalTime DESC
            LIMIT 200
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                long id = reader.GetInt64(0);
                long arrival = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                byte[]? payload = reader.IsDBNull(2) ? null : (byte[])reader[2];
                string appId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                if (payload is null || payload.Length == 0)
                {
                    continue;
                }

                string xml = System.Text.Encoding.UTF8.GetString(payload);
                var parsed = ParseToast(xml, appId);
                if (parsed is null)
                {
                    continue;
                }

                var (title, text, launch) = parsed.Value;
                string display = FriendlyAppName(appId);

                if (!string.IsNullOrWhiteSpace(appId))
                {
                    apps[appId] = display;
                }

                if (!IsAllowed(appId, display))
                {
                    continue;
                }

                string key = "wpn:" + id;
                result[key] = new IslandMessage
                {
                    Key = key,
                    Kind = MessageKind.Message,
                    Source = display,
                    AppId = appId,
                    Title = title,
                    Text = text,
                    LaunchTarget = launch,
                    ReceivedAt = ToTime(arrival),
                    FromWindowsNotifications = true,
                };
            }
            catch
            {
                // 单条坏了不影响其他
            }
        }

        return (result, apps);
    }

    /// <summary>从 toast XML 里取标题 / 正文 / 点击目标。</summary>
    private static (string title, string text, string launch)? ParseToast(string xml, string appId)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null)
            {
                return null;
            }

            string launch = root.Attribute("launch")?.Value ?? string.Empty;

            var texts = doc.Descendants()
                .Where(e => e.Name.LocalName == "text")
                .Select(e => Clean(e.Value))
                .Where(s => s.Length > 0)
                .ToList();

            if (texts.Count == 0)
            {
                // 小组件那种没有文本的通知，直接丢掉
                return null;
            }

            string title = texts[0];
            string text = texts.Count > 1 ? string.Join("\n", texts.Skip(1)) : string.Empty;

            // QQ 的通知里，launch 上有 peerName，比标题更准，但标题通常就是联系人，够用
            return (title, text, launch);
        }
        catch
        {
            return null;
        }
    }

    private static string Clean(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // toast 里经常带一堆空白和换行
        string cleaned = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        while (cleaned.Contains("\n\n"))
        {
            cleaned = cleaned.Replace("\n\n", "\n");
        }

        if (cleaned.Length > 400)
        {
            cleaned = cleaned[..400] + "…";
        }

        return cleaned;
    }

    /// <summary>FILETIME（100 纳秒，从 1601 年算起）→ DateTimeOffset。</summary>
    private static DateTimeOffset ToTime(long fileTime)
    {
        try
        {
            if (fileTime <= 0)
            {
                return DateTimeOffset.Now;
            }

            // 太大的值按 .NET ticks 处理，防止个别版本格式不同时崩掉
            if (fileTime > 2_000_000_000_000_000_000L)
            {
                return new DateTimeOffset(new DateTime(fileTime, DateTimeKind.Utc)).ToLocalTime();
            }

            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)).ToLocalTime();
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }

    private bool IsAllowed(string appId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return true;
        }

        // 只挡掉纯噪音（系统小组件之类），没有别的过滤规则
        foreach (string noise in DefaultNoise)
        {
            if (appId.Contains(noise, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 AUMID 变成人看得懂的名字。</summary>
    public static string FriendlyAppName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "系统";
        }

        string s = appId;
        string lower = s.ToLowerInvariant();

        if (lower == "qq" || lower.Contains("!qq") || lower.Contains("ntqq"))
        {
            return "QQ";
        }
        if (lower.Contains("wechat") || lower.Contains("weixin"))
        {
            return "微信";
        }
        if (lower.Contains("dingtalk"))
        {
            return "钉钉";
        }
        if (lower.Contains("defender") || lower.Contains("securitycenter"))
        {
            return "Windows 安全";
        }
        if (lower.Contains("widgets") || lower.Contains("webexperience"))
        {
            return "小组件";
        }
        if (lower.Contains("jianyingpro"))
        {
            return "剪映";
        }
        if (lower.Contains("traecn") || lower.Contains("byteDance"))
        {
            return "Trae";
        }
        if (lower.Contains("deepseek"))
        {
            return "DeepSeek";
        }
        if (lower.Contains("outlook") || lower.Contains("hxoutlook"))
        {
            return "Outlook";
        }
        if (lower.Contains("weiyun") || lower.Contains("tencent"))
        {
            return "腾讯";
        }
        if (lower.Contains("baidunetdisk"))
        {
            return "百度网盘";
        }
        if (lower.Contains("powershell") || lower.Contains("windowsterminal"))
        {
            return "终端";
        }

        // 形如 AAA.BBB_xxxxxxxx!AppName 的打包应用，取最后一段
        int bang = s.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < s.Length)
        {
            return s[(bang + 1)..];
        }

        // 去掉 _xxxxxxxx 后缀
        int underscore = s.IndexOf('_');
        if (underscore > 0)
        {
            s = s[..underscore];
        }

        return s;
    }

    public void Dispose()
    {
        _timer.Stop();

        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, true);
            }
        }
        catch
        {
            // 临时目录删不掉无所谓
        }
    }
}
