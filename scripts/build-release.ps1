param(
    [string]$Version = "0.3.3"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 이전 실행 중인 앱/구성 요소가 파일을 잠그면 패키징이 실패한다.
Get-Process -Name 'AsgardHuntMeter','asgard-core*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$v = $Version.TrimStart('v', 'V')
$dist = Join-Path $root "dist"
$appDir = Join-Path $dist "AsgardHuntMeter-$v"
$appZip = Join-Path $dist "AsgardHuntMeter-$v.zip"
$iconZip = Join-Path $dist "asgard-icons.zip"

# 공개 릴리스 ZIP에는 앱 실행 파일과 안내문만 포함한다.
# 구성 요소와 아이콘은 공식 서버/배포 채널에서 받는다.
# 커뮤니티 서버 바이너리는 운영자 전용이며 릴리스에 올리지 않는다.

Write-Host "[1/4] Build core" -ForegroundColor Cyan
Push-Location (Join-Path $root "core")
cargo build --release
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
Pop-Location

$coreName = "asgard-core-$v.exe"
$coreSrc = Join-Path $root "core\target\release\asgard-core.exe"
$coreOutDir = Join-Path $root "publish\core"
New-Item -ItemType Directory -Force -Path $coreOutDir | Out-Null
Copy-Item $coreSrc (Join-Path $coreOutDir $coreName) -Force

Write-Host "[2/4] GUI publish" -ForegroundColor Cyan
$publishDir = Join-Path $root "publish\app"
if (Test-Path $publishDir) {
    Get-ChildItem $publishDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
}
dotnet publish (Join-Path $root "gui\AsgardHuntMeter\AsgardHuntMeter.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$v `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed" }

Write-Host "[3/4] Assemble package" -ForegroundColor Cyan
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item (Join-Path $publishDir "*") $appDir -Recurse -Force
$readme = [IO.File]::ReadAllText((Join-Path $root "packaging\README.txt"), [Text.Encoding]::UTF8).Replace('{{VERSION}}', $v)
[IO.File]::WriteAllText((Join-Path $appDir "README.txt"), $readme, (New-Object Text.UTF8Encoding($true)))

if (-not (Test-Path (Join-Path $appDir "AsgardHuntMeter.exe"))) { throw "GUI exe missing" }
if (Get-ChildItem $appDir -Filter "asgard-core*.exe" -File) { throw "core must not be packaged" }

if (Test-Path $appZip) { Remove-Item $appZip -Force }
if (Test-Path $iconZip) { Remove-Item $iconZip -Force }
Compress-Archive -Path "$appDir\*" -DestinationPath $appZip -Force
Compress-Archive -Path (Join-Path $root "icons\*.png") -DestinationPath $iconZip -Force

$hash = (Get-FileHash $appZip -Algorithm SHA256).Hash.ToLower()
"$hash  AsgardHuntMeter-$v.zip" |
    Set-Content (Join-Path $dist "AsgardHuntMeter-$v.zip.sha256") -Encoding ascii -NoNewline

$coreHash = (Get-FileHash (Join-Path $coreOutDir $coreName) -Algorithm SHA256).Hash.ToLower()
$coreSize = (Get-Item (Join-Path $coreOutDir $coreName)).Length

$notes = [IO.File]::ReadAllText((Join-Path $root "packaging\release-notes.md"), [Text.Encoding]::UTF8).Replace('{{VERSION}}', $v)
[IO.File]::WriteAllText((Join-Path $dist "release-notes-$v.md"), $notes, (New-Object Text.UTF8Encoding($false)))

Write-Host "[4/4] Done" -ForegroundColor Green
Write-Host "Core for server upload: $coreOutDir\$coreName" -ForegroundColor DarkGray
Write-Host "Core SHA-256: $coreHash ($coreSize bytes)" -ForegroundColor DarkGray
Get-ChildItem $dist -File |
    Where-Object { $_.Name -like "*$v*" -or $_.Name -like "asgard-icons*" -or $_.Name -like "release-notes*" } |
    Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
