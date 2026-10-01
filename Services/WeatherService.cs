using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public enum WeatherKind
{
    Sunny,
    ClearNight,
    PartlyCloudy,
    Cloudy,
    Rain,
    HeavyRain,
    Snow,
    Thunder,
    Fog,
}

/// <summary>
/// 天气：Open-Meteo（免费、无需 API Key）。
/// 定位优先用配置的城市，其次按公网 IP 自动定位。
/// 图标是自己画的矢量图，不依赖任何图标字体，绝不会出现方块。
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    private readonly AppSettings _settings;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public WeatherInfo? Current { get; private set; }
    public WeatherKind Kind { get; private set; } = WeatherKind.Cloudy;
    public string? LastError { get; private set; }

    public event EventHandler<WeatherInfo>? Updated;

    public WeatherService(AppSettings settings)
    {
        _settings = settings;
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            // 默认 1 小时刷一次
            Interval = TimeSpan.FromMinutes(Math.Max(30, settings.WeatherRefreshMinutes)),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.0");
        return client;
    }

    public void Start()
    {
        _ = RefreshAsync();
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var (lat, lon, city) = await ResolveLocationAsync();
            if (lat is null || lon is null)
            {
                LastError = "无法定位";
                return;
            }

            string url =
                "https://api.open-meteo.com/v1/forecast" +
                $"?latitude={lat.Value.ToString("0.####", CultureInfo.InvariantCulture)}" +
                $"&longitude={lon.Value.ToString("0.####", CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code" +
                "&daily=temperature_2m_max,temperature_2m_min" +
                "&timezone=auto&forecast_days=1";

            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var current = root.GetProperty("current");
            double temp = current.GetProperty("temperature_2m").GetDouble();
            double feels = current.GetProperty("apparent_temperature").GetDouble();
            int humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            int code = current.GetProperty("weather_code").GetInt32();
            bool isDay = current.GetProperty("is_day").GetInt32() == 1;

            double tMax = temp, tMin = temp;
            if (root.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_max", out var maxArr) && maxArr.GetArrayLength() > 0)
                {
                    tMax = maxArr[0].GetDouble();
                }
                if (daily.TryGetProperty("temperature_2m_min", out var minArr) && minArr.GetArrayLength() > 0)
                {
                    tMin = minArr[0].GetDouble();
                }
            }

            var (condition, kind) = DescribeCode(code, isDay);
            Kind = kind;

            var info = new WeatherInfo
            {
                City = city,
                Temperature = temp,
                FeelsLike = feels,
                TempMax = tMax,
                TempMin = tMin,
                Humidity = humidity,
                Condition = condition,
                Glyph = condition,
                IsDay = isDay,
            };

            Current = info;
            LastError = null;
            Updated?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task<(double? lat, double? lon, string city)> ResolveLocationAsync()
    {
        // 手填的城市优先：IP 库经常把城市认错（比如把福州认成广州），
        // 这时候手填一个就准了。
        if (!string.IsNullOrWhiteSpace(_settings.City))
        {
            var geo = await GeocodeAsync(_settings.City.Trim());
            if (geo is not null)
            {
                return geo.Value;
            }
        }

        // 没手填才按出口 IP 定位
        if (_settings.AutoLocate)
        {
            var byIp = await LocateByIpAsync();
            if (byIp is not null)
            {
                return byIp.Value;
            }
        }

        return (null, null, string.Empty);
    }

    private static async Task<(double lat, double lon, string city)?> GeocodeAsync(string name)
    {
        try
        {
            string url =
                "https://geocoding-api.open-meteo.com/v1/search" +
                $"?name={Uri.EscapeDataString(name)}&count=1&language=zh&format=json";

            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                return null;
            }

            var first = results[0];
            double lat = first.GetProperty("latitude").GetDouble();
            double lon = first.GetProperty("longitude").GetDouble();
            string city = first.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
            return (lat, lon, city);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(double lat, double lon, string city)?> LocateByIpAsync()
    {
        // 多个源问一遍：IP 库各有各的误差，谁的第一个成功就用谁。
        // 首选 ip-api（lang=zh-CN，直接给中文城市名）。
        try
        {
            string json = await Http.GetStringAsync("http://ip-api.com/json/?lang=zh-CN");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) &&
                string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("lat", out var latEl) &&
                root.TryGetProperty("lon", out var lonEl))
            {
                string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(city) && root.TryGetProperty("regionName", out var r))
                {
                    city = r.GetString() ?? string.Empty;
                }
                return (latEl.GetDouble(), lonEl.GetDouble(), city);
            }
        }
        catch
        {
            // 试下一个
        }

        // 备选 ip.sb：对国内 IP 的城市判断往往更细
        try
        {
            string json = await Http.GetStringAsync("https://api.ip.sb/geoip");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("latitude", out var latEl) &&
                root.TryGetProperty("longitude", out var lonEl) &&
                latEl.ValueKind == JsonValueKind.Number &&
                lonEl.ValueKind == JsonValueKind.Number)
            {
                string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                return (latEl.GetDouble(), lonEl.GetDouble(), city);
            }
        }
        catch
        {
            // 试下一个
        }

        // 再备选 ipwho.is
        try
        {
            string json = await Http.GetStringAsync("https://ipwho.is/");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool ok = !root.TryGetProperty("success", out var okEl) || okEl.ValueKind != JsonValueKind.False;
            if (ok &&
                root.TryGetProperty("latitude", out var latEl) &&
                root.TryGetProperty("longitude", out var lonEl) &&
                latEl.ValueKind == JsonValueKind.Number &&
                lonEl.ValueKind == JsonValueKind.Number)
            {
                string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                return (latEl.GetDouble(), lonEl.GetDouble(), city);
            }
        }
        catch
        {
            // 放弃自动定位
        }

        return null;
    }

    /// <summary>WMO 天气代码 → 中文描述 + 图标种类。</summary>
    private static (string condition, WeatherKind kind) DescribeCode(int code, bool isDay) => code switch
    {
        0 => isDay ? ("晴", WeatherKind.Sunny) : ("晴夜", WeatherKind.ClearNight),
        1 => ("少云", WeatherKind.PartlyCloudy),
        2 => ("多云", WeatherKind.PartlyCloudy),
        3 => ("阴", WeatherKind.Cloudy),
        45 or 48 => ("雾", WeatherKind.Fog),
        51 or 53 or 55 => ("毛毛雨", WeatherKind.Rain),
        56 or 57 => ("冻雨", WeatherKind.Rain),
        61 => ("小雨", WeatherKind.Rain),
        63 => ("中雨", WeatherKind.Rain),
        65 => ("大雨", WeatherKind.HeavyRain),
        66 or 67 => ("冻雨", WeatherKind.HeavyRain),
        71 => ("小雪", WeatherKind.Snow),
        73 => ("中雪", WeatherKind.Snow),
        75 => ("大雪", WeatherKind.Snow),
        77 => ("雪粒", WeatherKind.Snow),
        80 => ("阵雨", WeatherKind.Rain),
        81 => ("强阵雨", WeatherKind.HeavyRain),
        82 => ("暴雨", WeatherKind.HeavyRain),
        85 or 86 => ("阵雪", WeatherKind.Snow),
        95 => ("雷阵雨", WeatherKind.Thunder),
        96 or 99 => ("雷暴冰雹", WeatherKind.Thunder),
        _ => ("未知", WeatherKind.Cloudy),
    };

    public void Dispose()
    {
        _timer.Stop();
    }
}

/// <summary>自己画的天气图标：不依赖图标字体，任何机器都显示正常。</summary>
public static class WeatherIcons
{
    private static readonly Dictionary<WeatherKind, ImageSource> Cache = new();

    public static ImageSource Get(WeatherKind kind)
    {
        if (Cache.TryGetValue(kind, out var cached))
        {
            return cached;
        }

        var image = Build(kind);
        Cache[kind] = image;
        return image;
    }

    /// <summary>按天气给的配色，用于温度块的淡淡底色。</summary>
    public static Color AccentColor(WeatherKind kind) => kind switch
    {
        WeatherKind.Sunny => Color.FromRgb(0xFF, 0xC1, 0x4D),
        WeatherKind.ClearNight => Color.FromRgb(0xC9, 0xD6, 0xFF),
        WeatherKind.PartlyCloudy => Color.FromRgb(0xFF, 0xD9, 0x8A),
        WeatherKind.Cloudy => Color.FromRgb(0xC3, 0xCE, 0xDC),
        WeatherKind.Rain => Color.FromRgb(0x74, 0xB6, 0xFF),
        WeatherKind.HeavyRain => Color.FromRgb(0x4C, 0x8F, 0xE8),
        WeatherKind.Snow => Color.FromRgb(0xCF, 0xE8, 0xFF),
        WeatherKind.Thunder => Color.FromRgb(0xB2, 0x8C, 0xFF),
        WeatherKind.Fog => Color.FromRgb(0xB8, 0xC0, 0xCC),
        _ => Colors.White,
    };

    private static ImageSource Build(WeatherKind kind)
    {
        var brush = new SolidColorBrush(Colors.White);
        var softBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        var group = new DrawingGroup();

        switch (kind)
        {
            case WeatherKind.Sunny:
                AddSun(group, brush, 12, 12, 4.6, 6.8, 9.6);
                break;

            case WeatherKind.ClearNight:
                AddMoon(group, brush);
                break;

            case WeatherKind.PartlyCloudy:
                AddSun(group, brush, 9.4, 8.6, 3.3, 5.1, 7.0);
                AddCloud(group, softBrush);
                break;

            case WeatherKind.Cloudy:
                AddCloud(group, brush);
                break;

            case WeatherKind.Rain:
                AddCloud(group, brush);
                AddDrops(group, brush, 3, 20.4);
                break;

            case WeatherKind.HeavyRain:
                AddCloud(group, brush);
                AddDrops(group, brush, 4, 20.0);
                break;

            case WeatherKind.Snow:
                AddCloud(group, brush);
                AddSnowflakes(group, brush);
                break;

            case WeatherKind.Thunder:
                AddCloud(group, brush);
                group.Children.Add(new GeometryDrawing(
                    brush,
                    null,
                    Geometry.Parse("M12.6,17.6 L9.4,21.0 L12.1,21.0 L10.6,23.6 L15.2,19.8 L12.4,19.8 L14.2,17.6 Z")));
                break;

            case WeatherKind.Fog:
                AddCloud(group, softBrush);
                AddFogLines(group, brush);
                break;
        }

        // 所有图标都补一个透明的 24x24 外框，保证它们的绘制范围完全一致，
        // 这样在界面里缩放时大小不会忽大忽小。
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void AddSun(DrawingGroup group, Brush brush, double cx, double cy, double core, double inner, double outer)
    {
        group.Children.Add(new GeometryDrawing(brush, null,
            new EllipseGeometry(new Point(cx, cy), core, core)));

        var pen = new Pen(brush, 1.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            double dx = Math.Cos(angle);
            double dy = Math.Sin(angle);
            var line = new LineGeometry(
                new Point(cx + dx * inner, cy + dy * inner),
                new Point(cx + dx * outer, cy + dy * outer));
            group.Children.Add(new GeometryDrawing(null, pen, line));
        }
    }

    private static void AddMoon(DrawingGroup group, Brush brush)
    {
        // 两个圆用 EvenOdd 相减，得到一个月牙
        var crescent = new GeometryGroup { FillRule = FillRule.EvenOdd };
        crescent.Children.Add(new EllipseGeometry(new Point(11.6, 12.4), 7.0, 7.0));
        crescent.Children.Add(new EllipseGeometry(new Point(15.6, 9.2), 6.2, 6.2));
        group.Children.Add(new GeometryDrawing(brush, null, crescent));

        // 两颗小星星
        group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(18.6, 6.6), 1.05, 1.05)));
        group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(20.4, 12.2), 0.8, 0.8)));
    }

    private static void AddCloud(DrawingGroup group, Brush brush)
    {
        var cloud = new GeometryGroup { FillRule = FillRule.Nonzero };
        cloud.Children.Add(new EllipseGeometry(new Point(9.6, 15.4), 3.5, 3.5));
        cloud.Children.Add(new EllipseGeometry(new Point(13.8, 13.9), 4.3, 4.3));
        cloud.Children.Add(new EllipseGeometry(new Point(17.3, 15.8), 2.9, 2.9));
        cloud.Children.Add(new RectangleGeometry(new Rect(6.4, 15.2, 12.2, 3.8), 1.9, 1.9));
        group.Children.Add(new GeometryDrawing(brush, null, cloud));
    }

    private static void AddDrops(DrawingGroup group, Brush brush, int count, double y)
    {
        var pen = new Pen(brush, 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        double startX = count switch
        {
            3 => 8.6,
            4 => 7.4,
            _ => 8.0,
        };

        for (int i = 0; i < count; i++)
        {
            double x = startX + i * 2.6;
            var line = new LineGeometry(new Point(x, y), new Point(x - 0.7, y + 3.0));
            group.Children.Add(new GeometryDrawing(null, pen, line));
        }
    }

    private static void AddSnowflakes(DrawingGroup group, Brush brush)
    {
        group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(8.8, 21.4), 1.15, 1.15)));
        group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(12.4, 22.6), 1.15, 1.15)));
        group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(16.0, 21.2), 1.15, 1.15)));
    }

    private static void AddFogLines(DrawingGroup group, Brush brush)
    {
        var pen = new Pen(brush, 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        group.Children.Add(new GeometryDrawing(null, pen,
            new LineGeometry(new Point(7.2, 20.6), new Point(17.4, 20.6))));
        group.Children.Add(new GeometryDrawing(null, pen,
            new LineGeometry(new Point(8.8, 23.6), new Point(16.2, 23.6))));
    }
}
