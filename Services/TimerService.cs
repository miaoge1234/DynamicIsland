using System.Windows.Threading;

namespace DynamicIsland.Services;

public enum IslandTimerKind
{
    Countdown,
    Alarm,
}

/// <summary>
/// 倒计时 / 闹钟。200ms 跳一次，进度环走得顺，不是一秒一跳那种卡顿感。
/// </summary>
public sealed class TimerService : IDisposable
{
    private readonly DispatcherTimer _ticker;
    private DateTimeOffset _endsAt;
    private TimeSpan _total;

    public TimerService(Dispatcher dispatcher)
    {
        _ticker = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _ticker.Tick += (_, _) => Tick();
    }

    public bool IsActive { get; private set; }
    public IslandTimerKind Kind { get; private set; } = IslandTimerKind.Countdown;
    public string Label { get; private set; } = string.Empty;
    public TimeSpan Remaining { get; private set; }

    /// <summary>距离结束还剩多少比例（1 → 0），给进度条用。</summary>
    public double Progress => _total > TimeSpan.Zero
        ? Math.Clamp(Remaining.TotalMilliseconds / _total.TotalMilliseconds, 0, 1)
        : 0;

    /// <summary>每 200ms 一次，给界面刷进度。</summary>
    public event EventHandler? Tick2;

    /// <summary>时间到了。</summary>
    public event EventHandler? Finished;

    public void StartCountdown(TimeSpan duration, string label = "")
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        Kind = IslandTimerKind.Countdown;
        _total = duration;
        _endsAt = DateTimeOffset.Now + duration;
        Label = label;
        IsActive = true;
        Remaining = duration;
        _ticker.Start();
        Tick2?.Invoke(this, EventArgs.Empty);
    }

    public void StartAlarm(DateTimeOffset when, string label = "")
    {
        var delta = when - DateTimeOffset.Now;
        if (delta <= TimeSpan.Zero)
        {
            // 时间已经过了，当成明天
            delta += TimeSpan.FromDays(1);
        }

        Kind = IslandTimerKind.Alarm;
        _total = delta;
        _endsAt = when;
        Label = label;
        IsActive = true;
        Remaining = delta;
        _ticker.Start();
        Tick2?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _ticker.Stop();
        IsActive = false;
        Remaining = TimeSpan.Zero;
    }

    private void Tick()
    {
        if (!IsActive)
        {
            return;
        }

        var remaining = _endsAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            _ticker.Stop();
            IsActive = false;
            Remaining = TimeSpan.Zero;
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        Remaining = remaining;
        Tick2?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把剩余时间格式化成 05:32 或 1:05:32。</summary>
    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        int totalHours = (int)time.TotalHours;
        return totalHours > 0
            ? $"{totalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    public void Dispose()
    {
        _ticker.Stop();
    }
}
