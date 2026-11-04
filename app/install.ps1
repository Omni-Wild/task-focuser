<#
.SYNOPSIS
    Builds Task Focuser v2 and installs it: the one command to run.

.DESCRIPTION
    1. Build (build.ps1).
    2. Stop the old Python "Task Focuser" and any running TaskFocuser.
    3. Copy dist\TaskFocuser.exe to the install folder.
    4. Start with Windows: HKCU Run value "Task Focuser" (the app's tray toggle uses the same name).
    5. Start-menu shortcut "Task Focuser".
    6. Launch the installed exe.
    The note text of the old app carries over: both read %APPDATA%\Task Focuser\state.json.

.PARAMETER InstallDir
    Where the exe goes. Default C:\Tools\TaskFocuser.
.PARAMETER NoAutostart
    Leave the Run key alone.
.PARAMETER NoLaunch
    Do not start the app at the end.
.PARAMETER SkipBuild
    Install the dist\TaskFocuser.exe that is already there.
.PARAMETER ExePath
    Install this exe (a downloaded release) instead of building: no .NET SDK needed. Implies -SkipBuild.
.PARAMETER DryRun
    Build, then only print what the other steps would do. Nothing is stopped, copied or written.
#>
param(
    [string]$InstallDir = 'C:\Tools\TaskFocuser',
    [switch]$NoAutostart,
    [switch]$NoLaunch,
    [switch]$SkipBuild,
    [string]$ExePath,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appDir = $PSScriptRoot
# A relative -InstallDir would otherwise go unresolved into the Run key and the shortcut.
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$distExe = Join-Path $appDir 'dist\TaskFocuser.exe'
if ($ExePath) { $distExe = (Resolve-Path -LiteralPath $ExePath).Path; $SkipBuild = $true }
$target = Join-Path $InstallDir 'TaskFocuser.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'Task Focuser'
$runValue = '"' + $target + '"'
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = Join-Path $programs 'Task Focuser.lnk'
$statePath = Join-Path $env:APPDATA 'Task Focuser\state.json'
$manual = @()   # commands he has to run himself when a step was refused

function Write-Step { param([string]$Text) Write-Host ''; Write-Host "== $Text" }
function Write-Dry { param([string]$Text) Write-Host "[dry run] would $Text" }

if ($DryRun) { Write-Host 'Dry run: only the build runs for real.' }

# --- 1. build ------------------------------------------------------------------------------
Write-Step '1/6 Build'
if ($SkipBuild) {
    Write-Host 'Skipped (-SkipBuild).'
} else {
    & (Join-Path $appDir 'build.ps1')
}
if (-not (Test-Path -LiteralPath $distExe)) { throw "Nothing to install: $distExe is missing." }

# --- 2. stop running instances --------------------------------------------------------------
Write-Step '2/6 Stop running instances'
$procs = @(Get-Process -Name 'Task Focuser', 'TaskFocuser' -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Host 'None running.' }

# The new app gets a polite --quit first so it flushes its state; forwarding through its own
# exe guarantees the same single-instance protocol.
$newOnes = @($procs | Where-Object { $_.Name -eq 'TaskFocuser' })
if ($newOnes.Count -gt 0) {
    $quitExe = $null
    try { $quitExe = $newOnes[0].Path } catch { $quitExe = $null }
    if (-not $quitExe) { $quitExe = $distExe }
    if ($DryRun) {
        Write-Dry "ask the running TaskFocuser to quit: `"$quitExe`" --quit"
    } else {
        try {
            Write-Host "Asking TaskFocuser to quit (state is flushed on quit)."
            $q = Start-Process -FilePath $quitExe -ArgumentList '--quit' -PassThru
            $null = $q.WaitForExit(5000)
            foreach ($p in $newOnes) { try { $null = $p.WaitForExit(3000) } catch { } }
        } catch {
            Write-Host "The --quit request failed: $($_.Exception.Message)"
        }
    }
}

foreach ($p in $procs) {
    $alive = $true
    try { $p.Refresh(); $alive = -not $p.HasExited } catch { $alive = $false }
    if (-not $alive) { Write-Host "Exited: $($p.Name) (pid $($p.Id))"; continue }
    $path = '?'
    try { if ($p.Path) { $path = $p.Path } } catch { }
    if ($DryRun) { Write-Dry "stop $($p.Name) (pid $($p.Id)) $path"; continue }
    try {
        Stop-Process -Id $p.Id -Force
        $null = $p.WaitForExit(3000)
        Write-Host "Stopped: $($p.Name) (pid $($p.Id)) $path"
    } catch {
        # The old app runs as a PyInstaller pair: stopping one can take the other down first.
        $gone = $false
        try { $p.Refresh(); $gone = $p.HasExited } catch { $gone = $true }
        if ($gone) { Write-Host "Exited: $($p.Name) (pid $($p.Id))"; continue }
        $cmd = "Stop-Process -Name `"$($p.Name)`" -Force"
        Write-Warning "Could not stop $($p.Name) (pid $($p.Id)): $($_.Exception.Message)"
        Write-Host "Run this yourself, then run install.ps1 -SkipBuild again if the copy below fails:"
        Write-Host "    $cmd"
        $manual += $cmd
    }
}

# --- 3. copy ---------------------------------------------------------------------------------
Write-Step "3/6 Copy to $InstallDir"
if ($DryRun) {
    Write-Dry "create $InstallDir and copy $distExe to $target"
} else {
    if (-not (Test-Path -LiteralPath $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
    # A just-stopped exe can hold its file for a moment; retry for about 2 s.
    $copied = $false
    $lastError = ''
    for ($i = 0; $i -lt 10 -and -not $copied; $i++) {
        try {
            Copy-Item -LiteralPath $distExe -Destination $target -Force
            $copied = $true
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 200
        }
    }
    if (-not $copied) {
        throw "Could not copy to $target (still in use?): $lastError"
    }
    $mb = [Math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
    Write-Host "Copied: $target ($mb MB)"
}

# --- 4. start with Windows -------------------------------------------------------------------
Write-Step '4/6 Start with Windows'
$autostart = 'unchanged'
if ($NoAutostart) {
    Write-Host 'Skipped (-NoAutostart).'
    try {
        $existing = (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction Stop).$runName
        if ($existing -ne $runValue) { Write-Warning "The Run value '$runName' points elsewhere: $existing" }
    } catch { }
} elseif ($DryRun) {
    Write-Dry "set $runKey\$runName = $runValue"
    $autostart = 'on (dry run)'
} else {
    Set-ItemProperty -LiteralPath $runKey -Name $runName -Value $runValue
    Write-Host "Set: $runKey\$runName = $runValue"
    $autostart = 'on'
}

# --- 5. Start-menu shortcut ------------------------------------------------------------------
Write-Step '5/6 Start-menu shortcut'
# The old Python app left its own shortcut; it would keep launching the old exe from Start.
$oldLnk = Join-Path $programs 'Task Focuser.exe.lnk'
$shell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $oldLnk) {
    $oldTarget = $shell.CreateShortcut($oldLnk).TargetPath
    if ($oldTarget -like '*LLM-Task_Focuser*') {
        if ($DryRun) { Write-Dry "remove the old app's shortcut $oldLnk ($oldTarget)" }
        else {
            Remove-Item -LiteralPath $oldLnk -Force
            Write-Host "Removed the old app's shortcut: $oldLnk"
        }
    }
}
# The old README told him to autostart it with a shortcut in shell:startup; both apps would start at logon.
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
foreach ($lnkFile in (Get-ChildItem -LiteralPath $startup -Filter *.lnk -ErrorAction SilentlyContinue)) {
    $t = $shell.CreateShortcut($lnkFile.FullName).TargetPath
    if ($t -like '*LLM-Task_Focuser*') {
        if ($DryRun) { Write-Dry "remove the old app's startup shortcut $($lnkFile.FullName) ($t)" }
        else {
            Remove-Item -LiteralPath $lnkFile.FullName -Force
            Write-Host "Removed the old app's startup shortcut: $($lnkFile.FullName)"
        }
    }
}
if ($DryRun) {
    Write-Dry "create $shortcut -> $target"
} else {
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = "$target,0"
    $lnk.Description = 'Task Focuser: the post-it note for what you are focusing on now'
    $lnk.Save()
    Write-Host "Created: $shortcut"
}

# --- 6. launch -------------------------------------------------------------------------------
Write-Step '6/6 Launch'
if ($NoLaunch) {
    Write-Host 'Skipped (-NoLaunch).'
} elseif ($DryRun) {
    Write-Dry "start $target"
} else {
    Start-Process -FilePath $target -WorkingDirectory $InstallDir
    Write-Host "Started: $target"
}

# --- summary ---------------------------------------------------------------------------------
Write-Host ''
Write-Host '== Summary'
Write-Host "Installed exe : $target"
Write-Host "Autostart     : $autostart"
Write-Host "Shortcut      : $shortcut"
Write-Host "State file    : $statePath"
if (Test-Path -LiteralPath $statePath) {
    Write-Host '                (found: the old note text, colour, position and opacity carry over)'
} else {
    Write-Host '                (not there yet: the app creates it on first change)'
}
if ($manual.Count -gt 0) {
    Write-Host 'Still to do by hand:'
    foreach ($m in $manual) { Write-Host "    $m" }
}
if ($DryRun) { Write-Host 'Dry run: nothing was stopped, copied or written.' }
