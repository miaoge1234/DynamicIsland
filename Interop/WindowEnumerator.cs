using System.Diagnostics;
using System.Text;

namespace DynamicIsland.Interop;

/// <summary>一个可见的顶层窗口快照。</summary>
internal sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, string ProcessName, uint ProcessId);

/// <summary>枚举顶层窗口，并按进程名缓存，避免每次都查进程。</summary>
internal static class WindowEnumerator
{
    private static readonly Dictionary<uint, string> ProcessNameCache = new();
    private static readonly object CacheLock = new();

    public static List<WindowInfo> EnumerateVisible()
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            int len = NativeMethods.GetWindowTextLengthW(hWnd);
            if (len <= 0)
            {
                return true;
            }

            var sb = new StringBuilder(len + 2);
            NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            string cls = GetClassName(hWnd);
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string proc = GetProcessName(pid);

            result.Add(new WindowInfo(hWnd, title, cls, proc, pid));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int n = NativeMethods.GetClassNameW(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hWnd);
        if (len <= 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder(len + 2);
        NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetProcessName(uint pid)
    {
        lock (CacheLock)
        {
            if (ProcessNameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        string name = string.Empty;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch
        {
            // 进程可能已退出或权限不足
        }

        if (!string.IsNullOrEmpty(name))
        {
            lock (CacheLock)
            {
                ProcessNameCache[pid] = name;
                if (ProcessNameCache.Count > 512)
                {
                    ProcessNameCache.Clear();
                }
            }
        }

        return name;
    }
}
