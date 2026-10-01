using System.Windows.Interop;

namespace DynamicIsland.Interop;

/// <summary>
/// 挂一个 Shell 钩子，接收任务栏"窗口请求注意"（闪烁）通知。
/// QQ 收到消息 / 来电话时会让窗口闪烁，这是唯一不依赖读窗口标题的信号，
/// 即使聊天窗口没打开也能收到。任意程序都会触发，调用方自己按进程过滤。
/// </summary>
internal sealed class ShellHook : IDisposable
{
    private const int HSHELL_FLASH = 0x8006;
    private const int HSHELL_RUDEAPPACTIVATED = 0x8004;

    private readonly HwndSource _source;
    private readonly uint _shellHookMessage;
    private bool _registered;

    /// <summary>某个窗口请求注意（闪烁）。参数是那个窗口的句柄。</summary>
    public event Action<IntPtr>? WindowFlash;

    /// <summary>某个窗口被激活（前台切换）。</summary>
    public event Action<IntPtr>? WindowActivated;

    public ShellHook(HwndSource source)
    {
        _source = source;
        _shellHookMessage = NativeMethods.RegisterWindowMessageW("SHELLHOOK");
        _source.AddHook(WndProc);
    }

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            _registered = NativeMethods.RegisterShellHookWindow(_source.Handle);
        }
        catch
        {
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_shellHookMessage != 0 && (uint)msg == _shellHookMessage)
        {
            int code = wParam.ToInt32();
            switch (code)
            {
                case HSHELL_FLASH:
                    WindowFlash?.Invoke(lParam);
                    break;
                case HSHELL_RUDEAPPACTIVATED:
                    WindowActivated?.Invoke(lParam);
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            try
            {
                NativeMethods.DeregisterShellHookWindow(_source.Handle);
            }
            catch
            {
                // 忽略
            }
            _registered = false;
        }

        _source.RemoveHook(WndProc);
    }
}
