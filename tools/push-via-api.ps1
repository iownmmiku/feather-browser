# 通过 GitHub REST API 推送当前工作区内容。
#
# 为什么需要它：这台机器上 git 的 HTTPS 通道（git-receive-pack）持续连不上，
# 而 REST API 一直正常。脚本用 API 完成「建 blob -> 建 tree -> 建 commit -> 更新引用」，
# 效果等同于一次 git push，只是绕开了 git 传输协议。
#
# 用法：.\tools\push-via-api.ps1 -Message "提交说明"
param(
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$Repo = 'iownmmiku/feather-browser',
    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
Push-Location $root
try {
    Write-Host "==> 仓库目录: $root" -ForegroundColor Cyan

    $remote = gh api "repos/$Repo/git/ref/heads/$Branch" | ConvertFrom-Json
    $remoteSha = $remote.object.sha
    $localSha = (git rev-parse HEAD).Trim()
    Write-Host "    远端 $remoteSha"
    Write-Host "    本地 $localSha"

    if ($remoteSha -eq $localSha) {
        Write-Host '    已经是同一个提交，无需推送' -ForegroundColor Yellow
        return
    }

    Write-Host '==> 上传文件内容（blob）' -ForegroundColor Cyan
    $treeItems = New-Object System.Collections.ArrayList
    $statusLines = @(git diff --name-status $remoteSha $localSha)

    foreach ($line in $statusLines) {
        if (-not $line) { continue }
        $parts = $line -split "`t", 2
        if ($parts.Count -lt 2) {
            Write-Host "    跳过无法解析的行: $line" -ForegroundColor Yellow
            continue
        }
        $code = $parts[0].Trim()
        $relPath = $parts[1].Trim()
        $fullPath = Join-Path $root $relPath

        if ($code -eq 'D') {
            [void]$treeItems.Add(@{ path = $relPath; sha = $null })
            Write-Host "    删除 $relPath" -ForegroundColor DarkGray
            continue
        }

        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Write-Host "    !! 找不到文件，跳过: $relPath" -ForegroundColor Red
            Write-Host "       （绝对路径 $fullPath）" -ForegroundColor DarkGray
            continue
        }

        $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($fullPath))
        $payload = @{ content = $base64; encoding = 'base64' } | ConvertTo-Json -Compress
        $temp = [IO.Path]::GetTempFileName()
        try {
            [IO.File]::WriteAllText($temp, $payload, [Text.UTF8Encoding]::new($false))
            $blob = gh api --method POST "repos/$Repo/git/blobs" --input $temp | ConvertFrom-Json
        }
        finally {
            Remove-Item $temp -Force -ErrorAction SilentlyContinue
        }

        [void]$treeItems.Add(@{ path = $relPath; sha = $blob.sha })
        $kb = [math]::Round((Get-Item -LiteralPath $fullPath).Length / 1KB, 1)
        Write-Host "    $relPath ($kb KB)"
    }

    if ($treeItems.Count -eq 0) {
        Write-Host '    没有需要上传的文件' -ForegroundColor Yellow
        return
    }

    Write-Host '==> 建 tree' -ForegroundColor Cyan
    $entries = @()
    foreach ($item in $treeItems) {
        $entries += @{
            path = $item.path
            mode = '100644'
            type = 'blob'
            sha  = $item.sha
        }
    }
    $treePayload = @{ base_tree = $remoteSha; tree = $entries } | ConvertTo-Json -Depth 6 -Compress
    $tempTree = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tempTree, $treePayload, [Text.UTF8Encoding]::new($false))
        $tree = gh api --method POST "repos/$Repo/git/trees" --input $tempTree | ConvertFrom-Json
    }
    finally {
        Remove-Item $tempTree -Force -ErrorAction SilentlyContinue
    }
    Write-Host "    tree: $($tree.sha)"

    Write-Host '==> 建 commit' -ForegroundColor Cyan
    $authorName = (git config user.name)
    $authorEmail = (git config user.email)
    if (-not $authorName) { $authorName = 'iownmmiku' }
    if (-not $authorEmail) { $authorEmail = 'iownmmiku@users.noreply.github.com' }

    $commitPayload = @{
        message = $Message
        tree    = $tree.sha
        parents = @($remoteSha)
        author  = @{ name = $authorName; email = $authorEmail }
    } | ConvertTo-Json -Depth 5 -Compress
    $tempCommit = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tempCommit, $commitPayload, [Text.UTF8Encoding]::new($false))
        $commit = gh api --method POST "repos/$Repo/git/commits" --input $tempCommit | ConvertFrom-Json
    }
    finally {
        Remove-Item $tempCommit -Force -ErrorAction SilentlyContinue
    }
    Write-Host "    commit: $($commit.sha)"

    Write-Host '==> 更新分支引用' -ForegroundColor Cyan
    $refPayload = @{ sha = $commit.sha; force = $false } | ConvertTo-Json -Compress
    $tempRef = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tempRef, $refPayload, [Text.UTF8Encoding]::new($false))
        gh api --method PATCH "repos/$Repo/git/refs/heads/$Branch" --input $tempRef | Out-Null
    }
    finally {
        Remove-Item $tempRef -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host "完成：远端 $Branch 现在指向 $($commit.sha)" -ForegroundColor Green
    Write-Host "注意：远端历史与本地不再一致（远端会多一个合并提交）。" -ForegroundColor Yellow
    Write-Host "网络恢复后执行 git fetch origin 再 git reset --soft origin/$Branch 对齐。" -ForegroundColor Yellow
}
finally {
    Pop-Location
}