# 本脚本假定仓库根目录就是本文件的上一级
$repo = Split-Path -Parent $PSScriptRoot

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class DpiProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string f, IntPtr h, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindResourceW(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    public static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll")]
    public static extern IntPtr LockResource(IntPtr d);
    [DllImport("kernel32.dll")]
    public static extern uint SizeofResource(IntPtr h, IntPtr res);
    [DllImport("shcore.dll")]
    public static extern int GetProcessDpiAwareness(IntPtr hProc, out int value);
    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    public static string GetManifest(string exe)
    {
        IntPtr h = LoadLibraryExW(exe, IntPtr.Zero, 0x00000002);
        if (h == IntPtr.Zero) return "(load failed, err=" + Marshal.GetLastWin32Error() + ")";
        IntPtr res = FindResourceW(h, new IntPtr(1), new IntPtr(24));
        if (res == IntPtr.Zero) return "(NO MANIFEST RESOURCE)";
        IntPtr data = LoadResource(h, res);
        IntPtr p = LockResource(data);
        uint size = SizeofResource(h, res);
        byte[] buf = new byte[size];
        Marshal.Copy(p, buf, 0, (int)size);
        return Encoding.UTF8.GetString(buf);
    }

    public static string Awareness(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "open failed";
        int v;
        int hr = GetProcessDpiAwareness(h, out v);
        if (hr != 0) return "hr=" + hr;
        if (v == 0) return "0 = UNAWARE";
        if (v == 1) return "1 = SYSTEM_AWARE";
        if (v == 2) return "2 = PER_MONITOR_AWARE";
        return "unknown " + v;
    }
}
'@

Add-Type -TypeDefinition $sig

$exe = "$repo\bin\Debug\net10.0-windows10.0.26100.0\win-x64\DynamicIsland.exe"

Write-Output "=== embedded manifest ==="
Write-Output ([DpiProbe]::GetManifest($exe))

Write-Output ""
Write-Output "=== process dpi awareness ==="
$p = Get-Process DynamicIsland -ErrorAction SilentlyContinue
if ($p) { Write-Output ("DynamicIsland: " + [DpiProbe]::Awareness([uint32]$p.Id)) } else { Write-Output "not running" }
Write-Output ("pwsh(self): " + [DpiProbe]::Awareness([uint32]$PID))
