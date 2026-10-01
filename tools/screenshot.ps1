param(
    [string]$Out = "",
    [int]$X = 0,
    [int]$Y = 0,
    [int]$Width = 0,
    [int]$Height = 0
)

# 抓整个屏幕（或指定区域）存成 PNG。默认存到仓库根目录下。
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $repo "shot.png" }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
if ($Width -le 0) { $Width = $bounds.Width }
if ($Height -le 0) { $Height = $bounds.Height }

$bmp = New-Object System.Drawing.Bitmap($Width, $Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.X + $X, $bounds.Y + $Y, 0, 0, (New-Object System.Drawing.Size($Width, $Height)))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out ($Width x $Height)"
