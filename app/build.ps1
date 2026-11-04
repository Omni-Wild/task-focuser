<#
.SYNOPSIS
    Builds dist\TaskFocuser.exe: one file, Release, win-x64.

.DESCRIPTION
    Regenerates Assets\monster.ico first when it is missing or older than
    Monster\Sprites.cs (or the icon script). Framework-dependent by default:
    the .NET 9 desktop runtime is on both machines and the exe stays small.
    -SelfContained bundles the runtime for a machine without it.
#>
param(
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved in the body: $PSScriptRoot is empty in param defaults under -File.
$appDir = $PSScriptRoot
$csproj = Join-Path $appDir 'TaskFocuser.csproj'
$sprites = Join-Path $appDir 'Monster\Sprites.cs'
$icon = Join-Path $appDir 'Assets\monster.ico'
$makeIcon = Join-Path $appDir 'tools\make-icon.ps1'
$dist = Join-Path $appDir 'dist'
$exe = Join-Path $dist 'TaskFocuser.exe'

Write-Host '== Icon'
$needIcon = -not (Test-Path -LiteralPath $icon)
if (-not $needIcon) {
    $iconTime = (Get-Item -LiteralPath $icon).LastWriteTimeUtc
    foreach ($dep in @($sprites, $makeIcon)) {
        if ((Test-Path -LiteralPath $dep) -and (Get-Item -LiteralPath $dep).LastWriteTimeUtc -gt $iconTime) { $needIcon = $true }
    }
}
if ($needIcon) {
    & $makeIcon -SpritesPath $sprites -OutPath $icon
} else {
    Write-Host "Up to date: $icon"
}

Write-Host '== Publish'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found on PATH. Install the .NET 9 SDK.'
}
# A stale exe from an earlier run must not pass for this run's output.
if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }

$sc = 'false'
if ($SelfContained) { $sc = 'true' }
$publishArgs = @(
    'publish', $csproj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $sc,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-o', $dist
)
if ($SelfContained) {
    # The bundled runtime is ~150 MB raw; compression roughly halves it at a small startup cost.
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}
Write-Host ("dotnet " + ($publishArgs -join ' '))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "dotnet publish succeeded but $exe is missing."
}

$mb = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
$kind = 'framework-dependent (needs the .NET 9 desktop runtime)'
if ($SelfContained) { $kind = 'self-contained' }
Write-Host "Built: $exe ($mb MB, $kind)"
