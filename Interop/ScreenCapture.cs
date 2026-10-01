using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Interop;

/// <summary>
/// 抓取窗口背后的屏幕区域，缩小 + 模糊，作为"液态玻璃"的底图。
///
/// 两个关键优化（不做的话空闲时能吃掉 10% 的 CPU）：
///  1) 模糊在 CPU 上用小图做（几十微秒），不用 WPF 的 BlurEffect 着色器，
///     这样底图重绘时只是一次纹理搬运；
///  2) 先对像素求哈希，画面没变就完全不更新，避免无意义的重绘。
/// </summary>
internal sealed class ScreenCapture
{
    private WriteableBitmap? _bitmap;
    private byte[] _buffer = Array.Empty<byte>();
    private byte[] _scratch = Array.Empty<byte>();
    private int _width;
    private int _height;
    private uint _lastHash;

    /// <summary>缩小的倍数：越大越模糊、越省。真正的柔化交给 CPU 盒子模糊。</summary>
    public int Downscale { get; set; } = 6;

    /// <summary>盒子模糊半径（在小图上，实际效果要乘 Downscale）</summary>
    public int BlurRadius { get; set; } = 3;

    /// <summary>模糊遍数，两三遍就足够接近高斯了</summary>
    public int BlurPasses { get; set; } = 2;

    /// <summary>上一次抓取里，抓屏 + 模糊花了多少毫秒</summary>
    public double LastCaptureMs { get; private set; }

    /// <summary>累计真正更新了底图的次数</summary>
    public int UpdateCount { get; private set; }

    public WriteableBitmap? Bitmap => _bitmap;

    public static Int32Rect VirtualScreenBounds()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0)
        {
            w = 1920;
            h = 1080;
        }
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>
    /// 抓取 <paramref name="physRect"/>（物理像素、屏幕坐标），缩小并模糊。
    /// 返回 true 表示底图内容变了（调用方需要重新渲染）；false 表示画面没变，
    /// 此时这张位图还是上一次的内容，可以继续用。
    /// </summary>
    public bool Capture(Int32Rect physRect)
    {
        if (physRect.Width <= 0 || physRect.Height <= 0)
        {
            return false;
        }

        int sw = Math.Max(1, physRect.Width / Downscale);
        int sh = Math.Max(1, physRect.Height / Downscale);
        bool sizeChanged = _bitmap is null || _width != sw || _height != sh;

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr oldBmp = IntPtr.Zero;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                return false;
            }

            hBmp = NativeMethods.CreateCompatibleBitmap(screenDc, sw, sh);
            if (hBmp == IntPtr.Zero)
            {
                return false;
            }

            oldBmp = NativeMethods.SelectObject(memDc, hBmp);

            // HALFTONE 的缩放在 GDI 里很慢（每次能到几十毫秒），
            // 反正后面要糊，用 COLORONCOLOR 快很多。
            NativeMethods.SetStretchBltMode(memDc, NativeMethods.COLORONCOLOR);

            bool ok = NativeMethods.StretchBlt(
                memDc, 0, 0, sw, sh,
                screenDc, physRect.X, physRect.Y, physRect.Width, physRect.Height,
                NativeMethods.SRCCOPY);

            if (!ok)
            {
                return false;
            }

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = sw,
                    biHeight = -sh, // 负数 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                }
            };

            int stride = sw * 4;
            int needed = stride * sh;
            if (_buffer.Length < needed)
            {
                _buffer = new byte[needed];
            }

            int lines = NativeMethods.GetDIBits(memDc, hBmp, 0, (uint)sh, _buffer, ref bmi, NativeMethods.DIB_RGB_COLORS);
            if (lines == 0)
            {
                return false;
            }

            // 画面没变就不动底图，省掉一次全岛重绘
            uint hash = Hash(_buffer, needed, stride);
            if (!sizeChanged && hash == _lastHash)
            {
                LastCaptureMs = stopwatch.Elapsed.TotalMilliseconds;
                return false;
            }

            // GetDIBits 对 32bpp BI_RGB 不写 alpha，必须自己补 255，否则整张图透明
            for (int i = 3; i < needed; i += 4)
            {
                _buffer[i] = 255;
            }

            Blur(_buffer, sw, sh, BlurRadius, BlurPasses);

            if (sizeChanged)
            {
                _bitmap = new WriteableBitmap(sw, sh, 96, 96, PixelFormats.Bgra32, null);
                _width = sw;
                _height = sh;
            }

            _bitmap!.WritePixels(new Int32Rect(0, 0, sw, sh), _buffer, stride, 0);
            _lastHash = hash;
            UpdateCount++;
            LastCaptureMs = stopwatch.Elapsed.TotalMilliseconds;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBmp);
            }
            if (hBmp != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBmp);
            }
            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>取样的 FNV 哈希，够快也够准。</summary>
    private static uint Hash(byte[] data, int length, int stride)
    {
        const uint prime = 16777619;
        uint hash = 2166136261;

        // 每行多抽几个点就够了，不必逐个字节
        int step = stride / 8;
        if (step < 4)
        {
            step = 4;
        }

        for (int i = 0; i < length; i += step)
        {
            hash = (hash ^ data[i]) * prime;
        }

        return hash;
    }

    /// <summary>可分离的盒子模糊（先横后竖），只处理 RGB，忽略 alpha。</summary>
    private static void Blur(byte[] buffer, int width, int height, int radius, int passes)
    {
        if (radius <= 0 || passes <= 0 || width < 3 || height < 3)
        {
            return;
        }

        int needed = width * height * 4;
        if (buffer.Length < needed)
        {
            return;
        }

        var temp = new byte[needed];

        for (int pass = 0; pass < passes; pass++)
        {
            BlurHorizontal(buffer, temp, width, height, radius);
            BlurVertical(temp, buffer, width, height, radius);
        }
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int window = radius * 2 + 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;

            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;

                // 初始窗口（左侧越界用第一列填充）
                for (int k = -radius; k <= radius; k++)
                {
                    int x = k < 0 ? 0 : (k >= width ? width - 1 : k);
                    sum += src[row + x * 4 + channel];
                }

                for (int x = 0; x < width; x++)
                {
                    dst[row + x * 4 + channel] = (byte)(sum / window);

                    int outIndex = x - radius;
                    int inIndex = x + radius + 1;
                    outIndex = outIndex < 0 ? 0 : (outIndex >= width ? width - 1 : outIndex);
                    inIndex = inIndex < 0 ? 0 : (inIndex >= width ? width - 1 : inIndex);

                    sum += src[row + inIndex * 4 + channel] - src[row + outIndex * 4 + channel];
                }
            }

            // alpha 直接抄过去
            for (int x = 0; x < width; x++)
            {
                dst[row + x * 4 + 3] = src[row + x * 4 + 3];
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int window = radius * 2 + 1;
        int stride = width * 4;

        for (int x = 0; x < width; x++)
        {
            int column = x * 4;

            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int y = k < 0 ? 0 : (k >= height ? height - 1 : k);
                    sum += src[column + y * stride + channel];
                }

                for (int y = 0; y < height; y++)
                {
                    dst[column + y * stride + channel] = (byte)(sum / window);

                    int outIndex = y - radius;
                    int inIndex = y + radius + 1;
                    outIndex = outIndex < 0 ? 0 : (outIndex >= height ? height - 1 : outIndex);
                    inIndex = inIndex < 0 ? 0 : (inIndex >= height ? height - 1 : inIndex);

                    sum += src[column + inIndex * stride + channel] - src[column + outIndex * stride + channel];
                }
            }

            for (int y = 0; y < height; y++)
            {
                dst[column + y * stride + 3] = src[column + y * stride + 3];
            }
        }
    }
}
