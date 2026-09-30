# 发布自包含版本并打包 Windows 安装程序。
#
# 自包含 = 把 .NET 运行时一起带上，用户不需要预装任何东西。
# WebView2 内核运行时按微软的许可不能随程序分发，所以只做检测 + 引导安装。
param(
    [string]$Version = '1.0.0',
    [switch]$SkipInstaller,
    [switch]$Trim
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\FeatherBrowser.csproj'
$pub = Join-Path $root 'src\bin\Release\net8.0-windows\win-x64\publish'
$dist = Join-Path $root 'dist'

Write-Host "==> 清理旧产物" -ForegroundColor Cyan
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "==> 自包含发布 (win-x64)" -ForegroundColor Cyan
$publishArgs = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    # 不做 IL 裁剪：WinForms 与 WebView2 都依赖 COM 封送存根（System.StubHelpers.*），
    # 裁剪会把它们当成未使用代码删掉，表现为程序能启动、但 WebView2 内核初始化抛
    # TypeLoadException: Could not load type 'System.StubHelpers.InterfaceMarshaler'。
    '-p:PublishTrimmed=false',
    "-p:Version=$Version",
    '--nologo'
)
if ($Trim) {
    Write-Host "    警告: -Trim 会裁掉 COM 封送存根导致 WebView2 起不来，仅供体积实验" -ForegroundColor Yellow
    $publishArgs += '-p:PublishTrimmed=true'
    $publishArgs += '-p:TrimMode=partial'
    $publishArgs += '-p:_SuppressWinFormsTrimError=true'
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$sizeMb = [math]::Round(((Get-ChildItem $pub -Recurse -File |
    Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "    发布目录: $sizeMb MB" -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "==> 已跳过安装程序打包" -ForegroundColor Yellow
    exit 0
}

$makensis = 'C:\Program Files (x86)\NSIS\makensis.exe'
if (-not (Test-Path $makensis)) {
    throw "未找到 NSIS: $makensis"
}

Write-Host "==> 编译安装程序" -ForegroundColor Cyan
$nsi = Join-Path $root 'tools\installer.nsi'
& $makensis "/DPRODUCT_VERSION=$Version" "/DSOURCE_DIR=$pub" "/DOUT_DIR=$dist" $nsi
if ($LASTEXITCODE -ne 0) { throw "makensis 失败，退出码 $LASTEXITCODE" }

Write-Host "==> 完成，产物：" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("    {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
}
