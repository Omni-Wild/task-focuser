param(
    [Parameter(Mandatory)] [string] $Original,
    [Parameter(Mandatory)] [string] $Rust,
    [Parameter(Mandatory)] [string] $OutputDir
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class CaptureWindows {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    public static string[] Describe(uint processId) {
        var lines = new List<string>();
        EnumWindows((h, p) => {
            uint id; GetWindowThreadProcessId(h, out id);
            if (id == processId && IsWindowVisible(h)) {
                Rect r; GetWindowRect(h, out r);
                if (r.Right <= r.Left || r.Bottom <= r.Top) return true;
                var title = new StringBuilder(512);
                GetWindowText(h, title, title.Capacity);
                lines.Add(String.Format("{0}: {1},{2} {3}x{4} {5}", h, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, title));
            }
            return true;
        }, IntPtr.Zero);
        return lines.ToArray();
    }
}
'@
function Save-Screen([string] $Path) {
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $image = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $image.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Changed-Pixels([string] $Before, [string] $After) {
    $a = [System.Drawing.Bitmap]::new($Before)
    $b = [System.Drawing.Bitmap]::new($After)
    try {
        if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { throw 'Desktop dimensions changed' }
        $changed = 0
        # Sample every fourth pixel; captures are retained at full resolution.
        for ($y = 0; $y -lt $a.Height; $y += 4) {
            for ($x = 0; $x -lt $a.Width; $x += 4) {
                if ($a.GetPixel($x, $y).ToArgb() -ne $b.GetPixel($x, $y).ToArgb()) { $changed++ }
            }
        }
        return $changed
    } finally { $a.Dispose(); $b.Dispose() }
}
$profile = 'visualparity'
$env:TASKFOCUSER_PROFILE = $profile
$dataDir = Join-Path $env:RUNNER_TEMP 'task-focuser-visual-data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$originalData = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Task Focuser'
New-Item -ItemType Directory -Force -Path $originalData | Out-Null
$originalState = Join-Path $originalData "state.$profile.json"
if (Test-Path -LiteralPath $originalState) { throw "Refusing to overwrite existing profile: $originalState" }
$runKey = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Task Focuser' -ErrorAction SilentlyContinue
if ($null -ne $runKey) { throw 'Refusing to launch WPF with an existing Task Focuser startup entry' }
$rustState = Join-Path $dataDir "state.$profile.json"
# This background is confined to the disposable runner session, never system wallpaper.
$backgroundCode = @'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
$form.BackColor = [System.Drawing.Color]::Black
$form.ShowInTaskbar = $false
$form.TopMost = $true
[System.Windows.Forms.Application]::Run($form)
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($backgroundCode))
$background = Start-Process powershell.exe -ArgumentList '-NoProfile', '-STA', '-EncodedCommand', $encoded -PassThru
Start-Sleep -Seconds 2
if ($background.HasExited) { throw 'Could not start the isolated black background' }
$metadata = @("Original commit: 67b0db11394ae1b265f11bfe013bea4631f8a9c8", "System DPI: $([CaptureWindows]::GetDpiForSystem())", "Virtual screen: $([System.Windows.Forms.SystemInformation]::VirtualScreen)", "Capture: GDI desktop composition, full virtual screen", "Original state: $originalState", "Rust state: $rustState")
$metadata += "Windows: $([Environment]::OSVersion.VersionString)"
foreach ($fontName in @('Segoe UI', 'Segoe Print', 'Comic Sans MS', 'Cascadia Code')) {
    $family = [System.Windows.Media.FontFamily]::new($fontName)
    $metadata += "WPF font $fontName baseline=$($family.Baseline) lineSpacing=$($family.LineSpacing) lineHeight16=$($family.LineSpacing * 16)"
}
$metadata += "Fonts: $(([System.Drawing.FontFamily]::Families | ForEach-Object Name) -join ', ')"
$metadata | Set-Content (Join-Path $OutputDir 'metadata.txt')
try {
    foreach ($scenario in @(
        @{ name = 'locked'; locked = $true; opacity = 100 },
        @{ name = 'unlocked'; locked = $false; opacity = 100 },
        @{ name = 'locked-50'; locked = $true; opacity = 50 }
    )) {
        $case = $scenario.name
        $locked = $scenario.locked
        $fixture = @{ version = 2; text = "Focus on this`nThen this"; x = 100; y = 100; color = '#FFF59E'; opacity = $scenario.opacity; locked = $locked; font = 'Segoe UI'; fontSize = 16; mode = 'note'; startWithWindows = $false; birdStays = $true; soundOn = $false; plantsOn = $true; bird = 'lennie' } | ConvertTo-Json
        foreach ($app in @('original', 'rust')) {
            $before = Join-Path $OutputDir "$case-$app-before.png"
            $after = Join-Path $OutputDir "$case-$app.png"
            Save-Screen $before
            if ($app -eq 'original') {
                $fixture | Set-Content -Encoding utf8 $originalState
                Remove-Item Env:TASKFOCUSER_DATA_DIR -ErrorAction SilentlyContinue
                $process = Start-Process -FilePath $Original -PassThru -RedirectStandardOutput (Join-Path $OutputDir "$case-$app-stdout.txt") -RedirectStandardError (Join-Path $OutputDir "$case-$app-stderr.txt")
            } else {
                $fixture | Set-Content -Encoding utf8 $rustState
                $env:TASKFOCUSER_DATA_DIR = $dataDir
                $process = Start-Process -FilePath $Rust -ArgumentList '--no-tray', '--no-sound' -PassThru -RedirectStandardOutput (Join-Path $OutputDir "$case-$app-stdout.txt") -RedirectStandardError (Join-Path $OutputDir "$case-$app-stderr.txt")
            }
            try {
                $windows = @()
                for ($i = 0; $i -lt 100 -and $windows.Count -eq 0; $i++) {
                    Start-Sleep -Milliseconds 100
                    if ($process.HasExited) { throw "$app exited before its window appeared: $($process.ExitCode)" }
                    $windows = @([CaptureWindows]::Describe($process.Id))
                }
                if ($windows.Count -eq 0) {
                    Save-Screen $after
                    throw "$app has no visible, nonempty window in the runner desktop; inspect the screenshot"
                }
                Start-Sleep -Seconds 3
                Save-Screen $after
                $changed = Changed-Pixels $before $after
                Add-Content (Join-Path $OutputDir 'metadata.txt') "$case $app PID $($process.Id); sampled changed pixels $changed; windows: $($windows -join '; ')"
                if ($changed -lt 50) { throw "$app desktop capture is blank or unchanged; inspect uploaded screenshots" }
            } finally {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                $process.WaitForExit()
            }
        }
    }
} finally {
    if (-not $background.HasExited) { Stop-Process -Id $background.Id -Force }
    Remove-Item -LiteralPath $originalState -Force -ErrorAction SilentlyContinue
    Remove-Item Env:TASKFOCUSER_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:TASKFOCUSER_PROFILE -ErrorAction SilentlyContinue
}
