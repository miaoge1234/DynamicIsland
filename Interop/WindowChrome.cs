using System.Windows;
using System.Windows.Interop;

namespace DynamicIsland.Interop;

/// <summary>无边框弹窗的窗口级小处理。</summary>
internal static class WindowChrome
{
    /// <summary>让 DWM 用系统圆角（自绘圆角时这个调用是无害的兜底）。</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int preference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // 老系统上没有这个属性，忽略
        }
    }
}
