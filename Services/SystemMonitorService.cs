using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// CPU / 内存占用检测。
/// CPU 用 GetSystemTimes 的两次采样差值算（不依赖性能计数器，也不怕计数器被玩坏）；
/// 内存用 GlobalMemoryStatusEx 的 dwMemoryLoad。
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly DispatcherTimer _timer;

    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _hasSample;

    public SystemMonitorService(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Sample();
    }

    /// <summary>CPU 占用百分比 0~100</summary>
    public double CpuPercent { get; private set; }

    /// <summary>内存占用百分比 0~100</summary>
    public double MemoryPercent { get; private set; }

    /// <summary>已用内存 GB</summary>
    public double UsedMemoryGb { get; private set; }

    /// <summary>总内存 GB</summary>
    public double TotalMemoryGb { get; private set; }

    public event EventHandler? Updated;

    public void Start()
    {
        Sample();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Sample()
    {
        try
        {
            // ---- CPU ----
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong idleNow = idle.ToUInt64();
                ulong kernelNow = kernel.ToUInt64();
                ulong userNow = user.ToUInt64();

                if (_hasSample)
                {
                    ulong idleDelta = idleNow - _lastIdle;
                    // 内核时间包含空闲时间，所以总量 = 内核 + 用户
                    ulong totalDelta = (kernelNow - _lastKernel) + (userNow - _lastUser);

                    if (totalDelta > 0)
                    {
                        double busy = (double)(totalDelta - idleDelta) / totalDelta * 100.0;
                        CpuPercent = Math.Clamp(busy, 0, 100);
                    }
                }

                _lastIdle = idleNow;
                _lastKernel = kernelNow;
                _lastUser = userNow;
                _hasSample = true;
            }

            // ---- 内存 ----
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };

            if (NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                MemoryPercent = status.dwMemoryLoad;
                TotalMemoryGb = status.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
                UsedMemoryGb = (status.ullTotalPhys - status.ullAvailPhys) / 1024.0 / 1024.0 / 1024.0;
            }

            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 读不到就算了，不影响其他功能
        }
    }

    /// <summary>按占用高低给个颜色，一眼能看出吃紧没有。</summary>
    public static System.Windows.Media.Color LoadColor(double percent)
    {
        if (percent >= 85)
        {
            return System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x7A);
        }

        if (percent >= 60)
        {
            return System.Windows.Media.Color.FromRgb(0xFF, 0xD1, 0x66);
        }

        return System.Windows.Media.Color.FromRgb(0x8C, 0xE0, 0x9A);
    }

    public void Dispose() => _timer.Stop();
}
