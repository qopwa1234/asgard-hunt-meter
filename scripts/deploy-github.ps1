param(
    [string]$RepoName = "asgard-hunt-meter",
    [string]$Visibility = "public",
    [string]$Version = "0.2.0"
)

$ErrorActionPreference = "Stop"
$gh = "C:\Program Files\GitHub CLI\gh.exe"
if (-not (Test-Path $gh)) { $gh = "gh" }

& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "GitHub 로그인이 필요합니다: gh auth login" -ForegroundColor Yellow
    exit 1
}

$login = (& $gh api user --jq .login).Trim()
$full = "$login/$RepoName"
Write-Host "repo: $full" -ForegroundColor Cyan

$hasRemote = (git remote get-url origin 2>$null)
if (-not $hasRemote) {
    $exists = (& $gh repo view $full --json name 2>$null)
    if ($LASTEXITCODE -ne 0) {
        & $gh repo create $full --$Visibility --source=. --remote=origin --push
    } else {
        git remote add origin "https://github.com/$full.git"
        git push -u origin HEAD
    }
} else {
    git push -u origin HEAD
}

& $gh repo edit $full `
    --description "아스가르드 사냥 수익 패시브 측정기 (Rust core + .NET GUI)" `
    --add-topic asgard --add-topic lineage --add-topic mmorpg --add-topic packet-capture `
    --add-topic rust --add-topic winforms --add-topic game-tools `
    --homepage "https://$login.github.io/$RepoName/"

try {
    & $gh api -X POST "repos/$full/pages" -f build_type=workflow *> $null
} catch {
    Write-Host "Pages가 이미 활성화되었거나 API 응답이 없습니다 (Settings에서 Source: GitHub Actions 확인)." -ForegroundColor Yellow
}

$zip = Join-Path $PSScriptRoot "..\dist\AsgardHuntMeter-$Version.zip"
if (Test-Path $zip) {
    $tag = "v$Version"
    & $gh release view $tag *> $null
    if ($LASTEXITCODE -ne 0) {
        & $gh release create $tag $zip --title "Asgard Hunt Meter $Version" `
            --notes "아이콘 팩 공유/커뮤니티 업로드, 버프 아이콘, GitHub Pages"
    } else {
        & $gh release upload $tag $zip --clobber
    }
}

Write-Host ""
Write-Host "완료:" -ForegroundColor Green
Write-Host "  repo    https://github.com/$full"
Write-Host "  pages   https://$login.github.io/$RepoName/"
Write-Host "  release https://github.com/$full/releases"
