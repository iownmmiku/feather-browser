# 发布自包含版本并打包 Windows 安装程序。
#
# 自包含 = 把 .NET 运行时一起带上，用户不需要预装 .NET。
# 内置 CEF Chromium；离线附带经微软签名验证的 VC++ 运行库。
param(
    [string]$Version,
    [string]$MakensisPath,
    [switch]$SkipInstaller
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
    # WinForms 和 C++/CLI 使用动态封送与反射，保留完整运行时。
    '-p:PublishTrimmed=false',
    "-p:Version=$Version",
    "-p:FileVersion=$assemblyVersion",
    "-p:AssemblyVersion=$assemblyVersion",
    '--nologo'
)
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

# VC++ 运行库随安装包离线提供。
$buildTools = Join-Path $dist 'build-tools'
New-Item -ItemType Directory -Force -Path $buildTools | Out-Null
$bootstrapper = Join-Path $buildTools 'vc_redist.x64.exe'
Write-Host "==> 获取并验证 VC++ 运行库" -ForegroundColor Cyan
$bootstrapperClient = New-Object System.Net.WebClient
$previousSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
try {
    [Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $bootstrapperClient.DownloadFile('https://aka.ms/vc14/vc_redist.x64.exe', $bootstrapper)
} finally {
    $bootstrapperClient.Dispose()
    [Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
    throw 'VC++ 运行库未通过微软 Authenticode 签名验证，停止打包。'
}

# 卸载只删除当前负载文件与空目录，避免误删安装目录中的其他文件。
$uninstallManifest = Join-Path $buildTools 'uninstall-files.nsh'
$uninstallLines = @()
Get-ChildItem -LiteralPath $pub -Recurse -File | ForEach-Object {
    $relativeFile = $_.FullName.Substring($pub.Length + 1).Replace('$', '$$').Replace('"', '$\"')
    $uninstallLines += 'Delete "$INSTDIR\' + $relativeFile + '"'
}
Get-ChildItem -LiteralPath $pub -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
    $relativeDirectory = $_.FullName.Substring($pub.Length + 1).Replace('$', '$$').Replace('"', '$\"')
    $uninstallLines += 'RMDir "$INSTDIR\' + $relativeDirectory + '"'
}
[IO.File]::WriteAllLines($uninstallManifest, $uninstallLines, [Text.UTF8Encoding]::new($false))
Write-Host "==> 编译安装程序" -ForegroundColor Cyan
$nsi = Join-Path $root 'tools\installer.nsi'
& $makensis "/DPRODUCT_VERSION=$Version" "/DPRODUCT_FILE_VERSION=$assemblyVersion" "/DSOURCE_DIR=$pub" "/DOUT_DIR=$dist" "/DVC_REDIST=$bootstrapper" "/DUNINSTALL_MANIFEST=$uninstallManifest" $nsi
if ($LASTEXITCODE -ne 0) { throw "makensis 失败，退出码 $LASTEXITCODE" }

Write-Host "==> 完成，产物：" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("    {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
}
