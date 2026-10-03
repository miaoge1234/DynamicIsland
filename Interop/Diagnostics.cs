using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Interop;

/// <summary>
/// 开发期自检：设置环境变量 DSH_ISLAND_DIAG=1 后，程序会把岛自己的画面
/// 渲染成 PNG，并把窗口几何写进日志，方便确认位置、尺寸和玻璃效果。
/// 正常使用不会触发。
/// </summary>
internal static class Diagnostics
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "diag");

    public static bool Enabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DSH_ISLAND_DIAG"));

    public static bool ExitAfter =>
        Environment.GetEnvironmentVariable("DSH_ISLAND_DIAG_EXIT") == "1";

    /// <summary>
    /// 演示模式：底图换成现场生成的渐变，消息 / 音乐 / 天气 / 性能全部换成示例数据。
    /// 专门用来出宣传截图 —— 能展示玻璃效果，又不会把用户桌面和私人消息拍进去。
    /// </summary>
    public static bool Demo =>
        Environment.GetEnvironmentVariable("DSH_ISLAND_DEMO") == "1";

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "diag.log"), message + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把可视元素渲染成 PNG（透明背景），即 WPF 自己画出来的样子。</summary>
    public static void Snapshot(FrameworkElement element, string name)
    {
        try
        {
            Directory.CreateDirectory(Dir);

            double width = element.ActualWidth > 1 ? element.ActualWidth : element.Width;
            double height = element.ActualHeight > 1 ? element.ActualHeight : element.Height;
            if (width < 1 || height < 1)
            {
                Write($"snapshot {name}: 尺寸无效 {width}x{height}");
                return;
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string path = Path.Combine(Dir, $"island-{name}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Write($"snapshot {name} -> {path} ({width:0}x{height:0} dip)");
        }
        catch (Exception ex)
        {
            Write($"snapshot {name} 失败: {ex.Message}");
        }
    }

    /// <summary>记录窗口真实几何（本进程是 DPI 感知的，拿到的是物理像素）。</summary>
    public static void LogGeometry(nint hwnd, Point islandTopLeft, Size islandSize, double dpiScale)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- {DateTime.Now:HH:mm:ss} ---");
            sb.AppendLine($"dpiScale={dpiScale}");

            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                sb.AppendLine($"window physical = L{rect.Left} T{rect.Top} W{rect.Width} H{rect.Height}");
                sb.AppendLine($"window centered? screenW={NativeMethods.GetSystemMetrics(0)} " +
                              $"left={(NativeMethods.GetSystemMetrics(0) - rect.Width) / 2} actual={rect.Left}");
            }

            sb.AppendLine($"island dip = ({islandTopLeft.X:0.#},{islandTopLeft.Y:0.#}) {islandSize.Width:0.#}x{islandSize.Height:0.#}");
            sb.AppendLine($"island physical = ({islandTopLeft.X * dpiScale:0.#},{islandTopLeft.Y * dpiScale:0.#}) " +
                          $"{islandSize.Width * dpiScale:0.#}x{islandSize.Height * dpiScale:0.#}");
            sb.AppendLine($"screen physical = {NativeMethods.GetSystemMetrics(0)}x{NativeMethods.GetSystemMetrics(1)}");
            sb.AppendLine($"dpi = {NativeMethods.GetDpiForWindow(hwnd)}");
            sb.AppendLine($"capture excluded = {Environment.GetEnvironmentVariable("DSH_ISLAND_NOEXCLUDE") != "1"}");

            Write(sb.ToString());
        }
        catch (Exception ex)
        {
            Write("LogGeometry 失败: " + ex.Message);
        }
    }
}
