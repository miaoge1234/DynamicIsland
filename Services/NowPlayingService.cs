using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// 通过 Windows 系统媒体控制（SMTC）读取当前播放的音乐。
/// 网易云音乐、QQ 音乐、Spotify 等都注册了 SMTC，因此歌名 / 歌手 / 封面 /
/// 进度都能拿到，并且可以直接控制播放。
/// 当 SMTC 没有会话时，退回读取网易云的窗口标题（只拿得到歌名歌手）。
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private TrackInfo _current = new();
    private BitmapImage? _artwork;
    private string _artworkKey = string.Empty;

    public NowPlayingService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += (_, _) => Tick();
    }

    /// <summary>曲目 / 播放状态变化。</summary>
    public event EventHandler<TrackInfo>? TrackChanged;

    /// <summary>仅进度变化（每秒一次），UI 用它更新进度条。</summary>
    public event EventHandler<TrackInfo>? ProgressTick;

    public TrackInfo Current => _current;
    public BitmapImage? Artwork => _artwork;
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            IsAvailable = _manager is not null;

            if (_manager is not null)
            {
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                _manager.SessionsChanged += OnSessionsChanged;
            }
        }
        catch
        {
            // 系统不支持 SMTC，退回窗口标题方案
            IsAvailable = false;
        }

        await RefreshAsync();
        _ticker.Start();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => Post(RefreshAsync);

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => Post(RefreshAsync);

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => Post(RefreshAsync);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => Post(RefreshAsync);

    private void Post(Func<Task> work)
    {
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await work();
            }
            catch
            {
                // 忽略瞬时错误
            }
        });
    }

    private void Tick()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline is null)
            {
                return;
            }

            var updated = Clone(_current,
                position: timeline.Position,
                duration: timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero);

            _current = updated;
            ProgressTick?.Invoke(this, updated);
        }
        catch
        {
            // 会话可能刚结束
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
    }

    public async Task RefreshAsync()
    {
        if (!await _refreshLock.WaitAsync(TimeSpan.FromSeconds(4)))
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = null;
            try
            {
                session = PickSession();
            }
            catch
            {
                session = null;
            }

            if (session is null)
            {
                AttachSession(null);

                // 没有 SMTC 会话时，尝试从网易云窗口标题兜底
                var fallback = TryFallbackFromWindowTitle();
                Apply(fallback, artwork: null, artworkKey: string.Empty, forceNotify: true);
                return;
            }

            if (!ReferenceEquals(session, _session))
            {
                AttachSession(session);
            }

            string appId = string.Empty;
            try
            {
                appId = session.SourceAppUserModelId ?? string.Empty;
            }
            catch
            {
                // 忽略
            }

            var props = await session.TryGetMediaPropertiesAsync();

            bool isPlaying = false;
            TimeSpan position = TimeSpan.Zero;
            TimeSpan duration = TimeSpan.Zero;
            try
            {
                var info = session.GetPlaybackInfo();
                isPlaying = info?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                // 忽略
            }

            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline is not null)
                {
                    position = timeline.Position;
                    if (timeline.EndTime > timeline.StartTime)
                    {
                        duration = timeline.EndTime - timeline.StartTime;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            string title = props?.Title ?? string.Empty;
            string artist = props?.Artist ?? string.Empty;
            string album = props?.AlbumTitle ?? string.Empty;

            var track = new TrackInfo
            {
                Title = title,
                Artist = artist,
                Album = album,
                SourceAppId = appId,
                SourceName = FriendlySourceName(appId),
                IsPlaying = isPlaying,
                Position = position,
                Duration = duration,
            };

            string artKey = $"{appId}|{title}|{artist}|{album}";
            BitmapImage? art = _artwork;
            if (!string.Equals(artKey, _artworkKey, StringComparison.Ordinal))
            {
                art = await TryLoadArtworkAsync(props?.Thumbnail);
                _artworkKey = artKey;
            }

            Apply(track, art, artKey, forceNotify: true);
        }
        catch
        {
            // 忽略：SMTC 在切歌瞬间容易抛
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// 挑一个会话。系统给的"当前会话"可能是抖音、浏览器之类，
    /// 这时如果网易云音乐等音乐软件正在播放，就优先显示音乐软件。
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? current = null;
        try
        {
            current = _manager.GetCurrentSession();
        }
        catch
        {
            current = null;
        }

        if (current is not null && IsMusicApp(current.SourceAppUserModelId))
        {
            return current;
        }

        try
        {
            foreach (var candidate in _manager.GetSessions())
            {
                if (!IsMusicApp(candidate.SourceAppUserModelId))
                {
                    continue;
                }

                var info = candidate.GetPlaybackInfo();
                if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return current;
    }

    private static bool IsMusicApp(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        string s = appId.ToLowerInvariant();
        return s.Contains("cloudmusic")
            || s.Contains("qqmusic")
            || s.Contains("spotify")
            || s.Contains("kugou")
            || s.Contains("kuwo")
            || s.Contains("kwmusic")
            || s.Contains("music")
            || s.Contains("potplayer")
            || s.Contains("vlc")
            || s.Contains("foobar")
            || s.Contains("aimp")
            || s.Contains("zune")
            || s.Contains("mediaplayer");
    }

    private void Apply(TrackInfo track, BitmapImage? artwork, string artworkKey, bool forceNotify)
    {
        bool changed =
            forceNotify
            || !string.Equals(track.Title, _current.Title, StringComparison.Ordinal)
            || !string.Equals(track.Artist, _current.Artist, StringComparison.Ordinal)
            || track.IsPlaying != _current.IsPlaying
            || !string.Equals(track.SourceAppId, _current.SourceAppId, StringComparison.Ordinal);

        _current = track;
        _artwork = artwork;

        if (changed)
        {
            TrackChanged?.Invoke(this, track);
        }
    }

    private static TrackInfo Clone(TrackInfo src, TimeSpan position, TimeSpan duration) => new()
    {
        Title = src.Title,
        Artist = src.Artist,
        Album = src.Album,
        SourceAppId = src.SourceAppId,
        SourceName = src.SourceName,
        IsPlaying = src.IsPlaying,
        IsFallbackSource = src.IsFallbackSource,
        Position = position,
        Duration = duration,
    };

    private static async Task<BitmapImage?> TryLoadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync();
            uint size = (uint)stream.Size;
            if (size == 0 || size > 12 * 1024 * 1024)
            {
                return null;
            }

            byte[] bytes;
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                bytes = new byte[size];
                reader.ReadBytes(bytes);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>SMTC 没有会话时的兜底：读播放器窗口标题，形如 "歌名 - 歌手"。</summary>
    private static TrackInfo TryFallbackFromWindowTitle()
    {
        try
        {
            foreach (var window in WindowEnumerator.EnumerateVisible())
            {
                if (!IsPlayerProcess(window.ProcessName))
                {
                    continue;
                }

                string title = window.Title.Trim();
                if (title.Length == 0)
                {
                    continue;
                }

                // 播放器空闲时标题就是程序名，忽略
                if (IsIdleTitle(title, window.ProcessName))
                {
                    continue;
                }

                string song = title;
                string artist = string.Empty;

                int sep = title.IndexOf(" - ", StringComparison.Ordinal);
                if (sep > 0)
                {
                    song = title[..sep].Trim();
                    artist = title[(sep + 3)..].Trim();
                }

                if (song.Length == 0)
                {
                    continue;
                }

                return new TrackInfo
                {
                    Title = song,
                    Artist = artist,
                    SourceAppId = window.ProcessName + ".exe",
                    SourceName = FriendlySourceName(window.ProcessName + ".exe"),
                    IsPlaying = true,
                    IsFallbackSource = true,
                };
            }
        }
        catch
        {
            // 忽略
        }

        return new TrackInfo();
    }

    private static bool IsIdleTitle(string title, string processName)
    {
        string t = title.Replace(" ", string.Empty);
        string p = processName.Replace(" ", string.Empty);
        return t.Equals(p, StringComparison.OrdinalIgnoreCase)
            || t.Contains("网易云音乐", StringComparison.OrdinalIgnoreCase) && !t.Contains(" - ")
            || t.Equals("QQ音乐", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Spotify", StringComparison.OrdinalIgnoreCase)
            || t.Equals("SpotifyFree", StringComparison.OrdinalIgnoreCase)
            || t.Equals("SpotifyPremium", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerProcess(string processName) =>
        processName.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("QQMusic", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("KuGou", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("KwMusic", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("Spotify", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("PotPlayerMini64", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("vlc", StringComparison.OrdinalIgnoreCase);

    /// <summary>把内部标识翻译成用户看得懂的名字。</summary>
    public static string FriendlySourceName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "媒体";
        }

        string s = appId.ToLowerInvariant();
        if (s.Contains("cloudmusic"))
        {
            return "网易云音乐";
        }
        if (s.Contains("qqmusic"))
        {
            return "QQ音乐";
        }
        if (s.Contains("spotify"))
        {
            return "Spotify";
        }
        if (s.Contains("kugou"))
        {
            return "酷狗音乐";
        }
        if (s.Contains("kuwo") || s.Contains("kwmusic"))
        {
            return "酷我音乐";
        }
        if (s.Contains("msedge"))
        {
            return "Edge";
        }
        if (s.Contains("chrome"))
        {
            return "Chrome";
        }
        if (s.Contains("firefox"))
        {
            return "Firefox";
        }
        if (s.Contains("potplayer"))
        {
            return "PotPlayer";
        }
        if (s.Contains("vlc"))
        {
            return "VLC";
        }
        if (s.Contains("zune") || s.Contains("mediaplayer"))
        {
            return "媒体播放器";
        }
        if (s.Contains("douyin"))
        {
            return "抖音";
        }
        if (s.Contains("bilibili"))
        {
            return "哔哩哔哩";
        }
        if (s.Contains("tencentvideo") || s.Contains("qqlive"))
        {
            return "腾讯视频";
        }
        if (s.Contains("iqiyi"))
        {
            return "爱奇艺";
        }

        string name = appId;
        int bang = name.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < name.Length)
        {
            name = name[(bang + 1)..];
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return name;
    }

    // ---------------- 播放控制 ----------------

    public async Task TogglePlayPauseAsync()
    {
        try
        {
            if (_session is not null)
            {
                await _session.TryTogglePlayPauseAsync();
            }
        }
        catch
        {
            // 忽略
        }
    }

    public async Task NextAsync()
    {
        try
        {
            if (_session is not null)
            {
                await _session.TrySkipNextAsync();
            }
        }
        catch
        {
            // 忽略
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (_session is not null)
            {
                await _session.TrySkipPreviousAsync();
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 拖动进度条跳转。走 SMTC 的 TryChangePlaybackPositionAsync，
    /// 网易云音乐等支持媒体控制的播放器都能生效；不支持的会静默失败。
    /// </summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        try
        {
            if (_session is null)
            {
                return false;
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            if (_current.Duration > TimeSpan.Zero && position > _current.Duration)
            {
                position = _current.Duration;
            }

            bool ok = await _session.TryChangePlaybackPositionAsync(position.Ticks);

            if (ok)
            {
                // 立刻反映到界面，别等下一次 tick
                _current = Clone(_current, position, _current.Duration);
                ProgressTick?.Invoke(this, _current);
            }

            return ok;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _ticker.Stop();

        try
        {
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }
            AttachSession(null);
        }
        catch
        {
            // 忽略
        }
    }
}
