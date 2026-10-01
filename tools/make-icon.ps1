# 把 assets\icon.png 转成多尺寸 .ico，并接到程序 / 安装程序两处。
#
# 用法：
#   .\tools\make-icon.ps1                       # 用 assets\icon.png
#   .\tools\make-icon.ps1 -Source D:\logo.png   # 指定别的图
param(
    [string]$Source = '',
    [switch]$SkipRelink
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Source) {
    $Source = Join-Path $root 'assets\icon.png'
}
if (-not (Test-Path $Source)) {
    throw "找不到图标图源：$Source`n请把 AI 生成的 PNG 放到 assets\icon.png，或用 -Source 指定路径。"
}

$assetsDir = Join-Path $root 'assets'
$icoPath = Join-Path $assetsDir 'icon.ico'
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Write-Host "==> 图源: $Source" -ForegroundColor Cyan
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($Source)
Write-Host "    原图: $($img.Width)x$($img.Height)"
if ($img.Width -lt 256 -or $img.Height -lt 256) {
    Write-Host "    警告: 建议至少 256x256，否则大尺寸图标会发虚" -ForegroundColor Yellow
}
$img.Dispose()

# 用独立的转换器（tools\iconmaker），不依赖主工程。
# 先编译再直接调用 exe：比 dotnet run 少一层参数转发，避免 --nologo 被当成程序参数。
Write-Host "==> 转换多尺寸 ICO (16/24/32/48/64/128/256)" -ForegroundColor Cyan
$proj = Join-Path $PSScriptRoot 'iconmaker\IconMaker.csproj'
& dotnet build $proj -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw '转换器编译失败' }

$maker = Join-Path $PSScriptRoot 'iconmaker\bin\Release\net8.0-windows\IconMaker.exe'
if (-not (Test-Path $maker)) { throw "找不到转换器: $maker" }

& $maker $Source $icoPath
if ($LASTEXITCODE -ne 0) { throw "图标转换失败（退出码 $LASTEXITCODE）" }

if ($SkipRelink) {
    Write-Host "==> 已跳过接线" -ForegroundColor Yellow
    exit 0
}

# 接到工程：作为 ApplicationIcon 嵌进 exe（任务管理器 / 快捷方式会用它）
Write-Host "==> 接到工程（ApplicationIcon）" -ForegroundColor Cyan
$csproj = Join-Path $root 'src\FeatherBrowser.csproj'
$content = [System.IO.File]::ReadAllText($csproj)
if ($content -notmatch '<ApplicationIcon>') {
    $content = $content -replace '(<ApplicationManifest>app\.manifest</ApplicationManifest>)',
        "`$1`r`n    <!-- 图标由 tools\make-icon.ps1 从 assets\icon.png 生成 -->`r`n    <ApplicationIcon>..\assets\icon.ico</ApplicationIcon>"
    [System.IO.File]::WriteAllText($csproj, $content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    已写入 ApplicationIcon" -ForegroundColor Green
} else {
    Write-Host "    ApplicationIcon 已存在，跳过" -ForegroundColor DarkGray
}

# 接到安装程序：快捷方式与卸载项都用它
Write-Host "==> 接到安装程序（NSIS）" -ForegroundColor Cyan
$nsi = Join-Path $root 'tools\installer.nsi'
$nsiContent = [System.IO.File]::ReadAllText($nsi)
if ($nsiContent -notmatch 'assets\\icon\.ico') {
    $nsiContent = $nsiContent -replace '!define MUI_ICON "[^"]*"',
        '!define MUI_ICON "..\assets\icon.ico"'
    $nsiContent = $nsiContent -replace '!define MUI_UNICON "[^"]*"',
        '!define MUI_UNICON "..\assets\icon.ico"'
    # NSIS 脚本必须是 UTF-8 带 BOM
    [System.IO.File]::WriteAllText($nsi, $nsiContent, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "    已写入 MUI_ICON / MUI_UNICON" -ForegroundColor Green
} else {
    Write-Host "    MUI_ICON 已存在，跳过" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "完成。接下来重建即可：" -ForegroundColor Green
Write-Host "  dotnet build .\src\FeatherBrowser.csproj -c Release"
Write-Host "  .\tools\build-installer.ps1 -Version 1.3.0"