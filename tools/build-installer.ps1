# 发布自包含版本并打包 Windows 安装程序。
#
# 自包含 = 把 .NET 运行时一起带上，用户不需要预装 .NET。
# 本项目复用系统 WebView2 运行时，只做检测 + 引导安装。
param(
    [string]$Version,
    [string]$MakensisPath,
    [switch]$SkipInstaller,
    [switch]$Trim
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$proj = Join-Path $root 'src\FeatherBrowser.csproj'
$pub = Join-Path $root 'src\bin\Release\net8.0-windows\win-x64\publish'
$dist = Join-Path $root 'dist'

if (-not $Version) {
    [xml]$projectXml = Get-Content -LiteralPath $proj -Raw
    $Version = [string]($projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "版本号必须是 major.minor.patch[.revision]: $Version" }
$parsedVersion = [version]$Version
$assemblyVersion = '{0}.{1}.{2}.{3}' -f $parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, [Math]::Max(0, $parsedVersion.Revision)

# 编译前清空发布目录，避免安装包混入以前遗留的 DLL；保留其它版本安装包。
$publishTarget = [IO.Path]::GetFullPath($pub)
$workspacePrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $publishTarget.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录不在当前工作区内，拒绝清理: $publishTarget"
}
Write-Host "==> 清理发布目录: $publishTarget" -ForegroundColor Cyan
if (Test-Path -LiteralPath $publishTarget) { Remove-Item -LiteralPath $publishTarget -Recurse -Force }
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
    "-p:FileVersion=$assemblyVersion",
    "-p:AssemblyVersion=$assemblyVersion",
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

if ($MakensisPath) {
    $makensis = (Resolve-Path -LiteralPath $MakensisPath).Path
} else {
    $nsisCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
    $makensis = if ($nsisCommand) { $nsisCommand.Source } else { 'C:\Program Files (x86)\NSIS\makensis.exe' }
}
if (-not (Test-Path -LiteralPath $makensis)) {
    throw "未找到 NSIS: $makensis。可用 -MakensisPath 指定免安装版编译器。"
}

# 附带微软的 Evergreen 引导程序，避免安装时用不支持 HTTPS 的 NSISdl 下载。
# 内核本体仍复用系统环境；只有缺失且用户同意时才由引导程序联网安装。
$buildTools = Join-Path $dist 'build-tools'
New-Item -ItemType Directory -Force -Path $buildTools | Out-Null
$bootstrapper = Join-Path $buildTools 'MicrosoftEdgeWebview2Setup.exe'
Write-Host "==> 获取并验证 WebView2 引导程序" -ForegroundColor Cyan
$bootstrapperClient = New-Object System.Net.WebClient
$previousSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
try {
    [Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $bootstrapperClient.DownloadFile('https://go.microsoft.com/fwlink/p/?LinkId=2124703', $bootstrapper)
} finally {
    $bootstrapperClient.Dispose()
    [Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
    throw 'WebView2 引导程序未通过微软 Authenticode 签名验证，停止打包。'
}

Write-Host "==> 编译安装程序" -ForegroundColor Cyan
$nsi = Join-Path $root 'tools\installer.nsi'
& $makensis "/DPRODUCT_VERSION=$Version" "/DPRODUCT_FILE_VERSION=$assemblyVersion" "/DSOURCE_DIR=$pub" "/DOUT_DIR=$dist" "/DWEBVIEW2_BOOTSTRAPPER=$bootstrapper" $nsi
if ($LASTEXITCODE -ne 0) { throw "makensis 失败，退出码 $LASTEXITCODE" }

Write-Host "==> 完成，产物：" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("    {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
}
