# 本脚本假定仓库根目录就是本文件的上一级
$repo = Split-Path -Parent $PSScriptRoot

# 端到端验证：启动岛 → 推消息 → 抓图 → 推来电 → 抓图
$ErrorActionPreference = 'Stop'

$shot = "Join-Path $PSScriptRoot 'screenshot.ps1'"
$outDir = "$repo\tools\shots"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$port = 7788

function Grab([string]$name) {
    & $shot -Out (Join-Path $outDir "$name.png") -X 380 -Y 0 -Width 760 -Height 340 | Out-Null
    Write-Output "grabbed $name"
}

function Push([string]$path, [byte[]]$bytes, [string]$contentType) {
    $url = "http://127.0.0.1:$port/$path"
    try {
        if ($contentType) {
            $r = Invoke-RestMethod -Uri $url -Method Post -Body $bytes -ContentType $contentType -TimeoutSec 8
        } else {
            $r = Invoke-RestMethod -Uri $url -Method Post -Body $bytes -TimeoutSec 8
        }
        return ($r | ConvertTo-Json -Compress)
    } catch {
        return "FAILED: $($_.Exception.Message)"
    }
}

$exe = "$repo\bin\Debug\net10.0-windows10.0.26100.0\win-x64\DynamicIsland.exe"
Get-Process DynamicIsland -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 7
if ($p.HasExited) { throw "app exited early: $($p.ExitCode)" }

Write-Output "--- ping ---"
try { (Invoke-RestMethod -Uri "http://127.0.0.1:$port/ping" -TimeoutSec 8) | ConvertTo-Json -Compress }
catch { "ping FAILED: $($_.Exception.Message)" }

Grab "01-idle"

# A) 规范客户端：真 UTF-8 字节 + 声明 charset
Write-Output "--- A) UTF-8 + charset ---"
$jsonA = '{"source":"QQ","title":"张三","text":"在吗？晚上一起吃饭"}'
Write-Output ("response: " + (Push "message" ([System.Text.Encoding]::UTF8.GetBytes($jsonA)) "application/json; charset=utf-8"))
Start-Sleep -Milliseconds 1200
Grab "02-utf8"

# B) 老客户端：GB2312 字节，不声明 charset（中文 Windows 上很常见）
Write-Output "--- B) GB2312, no charset ---"
Start-Sleep -Milliseconds 1500
$jsonB = '{"source":"QQ","title":"李四","text":"会议改到三点了"}'
Write-Output ("response: " + (Push "message" ([System.Text.Encoding]::GetEncoding(936).GetBytes($jsonB)) $null))
Start-Sleep -Milliseconds 1200
Grab "03-gb2312"

# C) 来电
Write-Output "--- C) call ---"
Start-Sleep -Milliseconds 1500
$jsonC = '{"title":"王五"}'
Write-Output ("response: " + (Push "call" ([System.Text.Encoding]::UTF8.GetBytes($jsonC)) "application/json"))
Start-Sleep -Milliseconds 1200
Grab "04-call"

# D) 纯文本
Write-Output "--- D) plain text ---"
Start-Sleep -Milliseconds 1500
Write-Output ("response: " + (Push "message" ([System.Text.Encoding]::UTF8.GetBytes("构建完成了")) $null))
Start-Sleep -Milliseconds 1200
Grab "05-text"

# E) 错误路径
Write-Output "--- E) unknown path ---"
try { (Invoke-RestMethod -Uri "http://127.0.0.1:$port/nope" -TimeoutSec 8) | ConvertTo-Json -Compress }
catch { "expected 404: " + $_.Exception.Message }

Write-Output "--- still alive? ---"
if ($p.HasExited) { "APP DIED, code=$($p.ExitCode)" } else { "running, pid=$($p.Id)" }

Write-Output "--- error.log ---"
$log = Join-Path $env:APPDATA 'DynamicIsland\error.log'
if (Test-Path $log) { Get-Content $log -Tail 30 } else { "(none)" }

Write-Output "--- cpu/mem ---"
Get-Process DynamicIsland | Select-Object Id, CPU, @{n='MemMB';e={[math]::Round($_.WorkingSet64/1MB,1)}} | Format-Table -AutoSize
