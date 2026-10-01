# 截取轻羽浏览器窗口（含同进程的弹层/对话框），用于人工核对界面排版。
# 用法: screenshot.ps1 <exe> <输出png> <启动参数> <等待秒数>
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Arg = '',
  [int]$Wait = 11
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# 辅助类单独放 .cs，避免 here-string 与 C# 引号序列冲突
Add-Type -Path (Join-Path $PSScriptRoot 'WinShot.cs')

[WinShot]::SetProcessDPIAware() | Out-Null

$p = if ($Arg) {
  Start-Process -FilePath $Exe -ArgumentList $Arg -PassThru
} else {
  Start-Process -FilePath $Exe -PassThru
}

Start-Sleep -Seconds $Wait
if ($p.HasExited) { Write-Output "PROCESS EXITED: $($p.ExitCode)"; exit 1 }

$h = $p.MainWindowHandle
$tries = 0
while ($h -eq [IntPtr]::Zero -and $tries -lt 40) {
  Start-Sleep -Milliseconds 500
  $p.Refresh()
  if ($p.HasExited) { Write-Output 'PROCESS EXITED DURING WAIT'; exit 1 }
  $h = $p.MainWindowHandle
  $tries++
}
if ($h -eq [IntPtr]::Zero) {
  Write-Output 'NO WINDOW HANDLE'
  Stop-Process -Id $p.Id -Force
  exit 2
}

[WinShot]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
[WinShot]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1200

$r = New-Object 'WinShot+RECT'
[WinShot]::GetWindowRect($h, [ref]$r) | Out-Null
$mw = $r.Right - $r.Left
$mh = $r.Bottom - $r.Top
Write-Output ("main: {0},{1} {2}x{3} title='{4}'" -f $r.Left, $r.Top, $mw, $mh, [WinShot]::TitleOf($h))
if ($mw -le 0 -or $mh -le 0) {
  Write-Output 'BAD RECT'
  Stop-Process -Id $p.Id -Force
  exit 3
}

$canvas = New-Object System.Drawing.Bitmap($mw, $mh)
$cg = [System.Drawing.Graphics]::FromImage($canvas)
# 画布底色用深色：主窗口截图万一没覆盖到边缘（圆角外、边框等），
# 用浅灰会在深色界面旁边显出一条亮带，看起来像界面 bug。深色最不显眼。
$cg.Clear([System.Drawing.Color]::FromArgb(255, 22, 23, 26))

$main = New-Object System.Drawing.Bitmap($mw, $mh)
$g1 = [System.Drawing.Graphics]::FromImage($main)
$dc1 = $g1.GetHdc()
[WinShot]::Grab($h, $dc1) | Out-Null
$g1.ReleaseHdc($dc1)
$g1.Dispose()
$cg.DrawImage($main, 0, 0, $mw, $mh)
$main.Dispose()

$extra = [WinShot]::WindowsOf($p.Id, $h)
foreach ($wh in $extra) {
  if ($wh -eq $h) { continue }

  $rr = New-Object 'WinShot+RECT'
  [WinShot]::GetWindowRect($wh, [ref]$rr) | Out-Null
  $w = $rr.Right - $rr.Left
  $ht = $rr.Bottom - $rr.Top
  if ($w -le 0 -or $ht -le 0) { continue }

  $sub = New-Object System.Drawing.Bitmap($w, $ht)
  $g2 = [System.Drawing.Graphics]::FromImage($sub)
  $dc2 = $g2.GetHdc()
  [WinShot]::Grab($wh, $dc2) | Out-Null
  $g2.ReleaseHdc($dc2)
  $g2.Dispose()

  $x = $rr.Left - $r.Left
  $y = $rr.Top - $r.Top
  $cg.DrawImage($sub, $x, $y, $w, $ht)
  $sub.Dispose()
  Write-Output ("overlay: '{0}' at {1},{2} {3}x{4}" -f [WinShot]::TitleOf($wh), $x, $y, $w, $ht)
}

$cg.Dispose()
$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
Write-Output "saved: $Out"

Stop-Process -Id $p.Id -Force
Start-Sleep -Milliseconds 600
Write-Output 'done'
