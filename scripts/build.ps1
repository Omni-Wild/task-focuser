$ErrorActionPreference = 'Stop'
Set-Location (git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cargo build --locked --release -p task-focuser
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $PWD\target\release\task-focuser.exe"
