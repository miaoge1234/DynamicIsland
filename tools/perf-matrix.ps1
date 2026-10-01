# 本脚本假定仓库根目录就是本文件的上一级
$repo = Split-Path -Parent $PSScriptRoot

# 资源占用矩阵：逐个关掉组件，找出 CPU 去哪了
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:APPDATA 'DynamicIsland'
$settingsPath = Join-Path $dir 'settings.json'
$exe = "$repo\bin\Debug\net10.0-windows10.0.26100.0\win-x64\DynamicIsland.exe"

function Set-Setting($name, $value) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($null -eq $j.PSObject.Properties[$name]) {
        $j | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force
    } else {
        $j.$name = $value
    }
    $j | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding UTF8
}

function Measure-Idle([string]$label) {
    Get-Process DynamicIsland -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $p = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 10
    if ($p.HasExited) { Write-Output "$label -> DIED code=$($p.ExitCode)"; return }
    $p.Refresh(); $c1 = $p.CPU
    Start-Sleep -Seconds 15
    $p.Refresh(); $c2 = $p.CPU
    $cpu = ($c2 - $c1) / 15 * 100
    Write-Output ("{0,-42} CPU {1,6:N2}%   WS {2,6:N1} MB" -f $label, $cpu, ($p.WorkingSet64 / 1MB))
    Stop-Process -Id $p.Id -Force
}

# 1) 全开
Set-Setting 'EnableMusic' $true
Set-Setting 'EnableQqWatch' $true
Set-Setting 'EnableWeather' $true
Set-Setting 'BackdropRefreshMs' 700
Measure-Idle "1. all on (backdrop 700ms, qq, music)"

# 2) 关背景刷新
Set-Setting 'BackdropRefreshMs' 0
Measure-Idle "2. backdrop refresh OFF"

# 3) 再关 QQ 监听
Set-Setting 'EnableQqWatch' $false
Measure-Idle "3. + qq OFF"

# 4) 再关音乐
Set-Setting 'EnableMusic' $false
Measure-Idle "4. + music OFF"

# 5) 再关天气
Set-Setting 'EnableWeather' $false
Measure-Idle "5. + weather OFF (bare window)"

# 还原
Set-Setting 'EnableMusic' $true
Set-Setting 'EnableQqWatch' $true
Set-Setting 'EnableWeather' $true
Set-Setting 'BackdropRefreshMs' 700
Write-Output "settings restored"
