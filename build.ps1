$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location (Join-Path $root "core")
cargo build --release
$ok = ($LASTEXITCODE -eq 0)
Pop-Location
if ($ok) {
    Write-Host "OK: $root\core\target\release\asgard-core.exe"
} else {
    Write-Host "BUILD FAILED"
    exit 1
}
