using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// 开机自启：写 HKCU 的 Run 项，不需要管理员权限。
/// 单文件发布后 Environment.ProcessPath 就是那个 exe 的真实路径。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    /// <summary>当前是否已经设置了自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入或移除自启项。返回是否成功。</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("自启设置失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>启动时对一遍，保证注册表和实际状态一致（比如程序被移走了）。</summary>
    public static void SyncOnStartup(bool desired)
    {
        try
        {
            if (desired != IsEnabled())
            {
                Apply(desired);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
