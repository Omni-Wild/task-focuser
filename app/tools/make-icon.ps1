<#
.SYNOPSIS
    Draws the monster's idle frame into app\Assets\monster.ico (16, 24, 32, 48, 64 px).

.DESCRIPTION
    Reads Monster\Sprites.cs as text (no compile step): the Legend pairs
    ['c'] = "#RRGGBB", and the rows of the IdleOpen frame. When IdleOpen is not
    in the file yet (the skeleton's stub), a built-in placeholder grid is used
    instead, so the project always has an icon. The .ico is written by hand:
    one ICONDIR, one ICONDIRENTRY per size, each layer embedded as PNG (Vista+),
    because Icon.Save from one bitmap would give a single size only.

.PARAMETER SpritesPath
    The C# sprite file. Default: ..\Monster\Sprites.cs relative to this script.

.PARAMETER OutPath
    The icon to write. Default: ..\Assets\monster.ico relative to this script.

.PARAMETER PreviewPath
    Optional: also save the 64 px layer as a PNG here, to look at.
#>
param(
    [string]$SpritesPath,
    [string]$OutPath,
    [string]$PreviewPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Defaults resolve here, not in param(): $PSScriptRoot is empty in param defaults under -File.
if (-not $SpritesPath) { $SpritesPath = Join-Path $PSScriptRoot '..\Monster\Sprites.cs' }
if (-not $OutPath) { $OutPath = Join-Path $PSScriptRoot '..\Assets\monster.ico' }
# .NET IO resolves relative paths against the process directory, not PowerShell's location.
function Resolve-Full {
    param([string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path (Get-Location).Path $Path }
    return [IO.Path]::GetFullPath($Path)
}
$SpritesPath = Resolve-Full $SpritesPath
$OutPath = Resolve-Full $OutPath
if ($PreviewPath) { $PreviewPath = Resolve-Full $PreviewPath }

$Sizes = @(16, 24, 32, 48, 64)

# The fallback: a blue 12 x 10 rounded box with two white eyes, plus four legs below it.
$PlaceholderRows = @(
    '.oooooooooo.',
    'obbbbbbbbbbo',
    'obwwbbbbwwbo',
    'obwpbbbbwpbo',
    'obwpbbbbwpbo',
    'obbbbbbbbbbo',
    'obbllllllbbo',
    'obbllllllbbo',
    'obbbbbbbbbbo',
    '.oooooooooo.',
    '.oo.oo.oo.oo',
    '.oo.oo.oo.oo'
)
$PlaceholderLegend = @{
    '.' = ''
    'o' = '#1F4FBF'
    'b' = '#4F8DFF'
    'l' = '#9CC4FF'
    'w' = '#FFFFFF'
    'p' = '#1A1A2E'
}

function Read-SpriteSource {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "Sprites file not found: $Path"
        return $null
    }
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $block = [regex]::Match($text, 'IdleOpen\s*=\s*\{(.*?)\};', [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $block.Success) { return $null }
    $rows = @()
    foreach ($m in [regex]::Matches($block.Groups[1].Value, '"([^"]*)"')) { $rows += $m.Groups[1].Value }
    if ($rows.Count -eq 0) { return $null }
    $legend = New-Object System.Collections.Hashtable ([StringComparer]::Ordinal)  # c and C are different cells
    foreach ($m in [regex]::Matches($text, "\['(.)'\]\s*=\s*""(#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?|)""")) {
        $legend[$m.Groups[1].Value] = $m.Groups[2].Value
    }
    if (-not $legend.ContainsKey('.')) { $legend['.'] = '' }
    return @{ Rows = $rows; Legend = $legend }
}

function ConvertTo-Color {
    param([string]$Hex)
    if (-not $Hex) { return [System.Drawing.Color]::Transparent }
    $h = $Hex.TrimStart('#')
    if ($h.Length -eq 6) { $h = 'FF' + $h }
    elseif ($h.Length -eq 8) { $h = $h.Substring(6, 2) + $h.Substring(0, 6) }  # #RRGGBBAA -> AARRGGBB
    return [System.Drawing.Color]::FromArgb([Convert]::ToInt32($h, 16))
}

function New-Layer {
    param([string[]]$Rows, [hashtable]$Colors, [int]$Size)
    $gw = 0
    foreach ($r in $Rows) { if ($r.Length -gt $gw) { $gw = $r.Length } }
    $gh = $Rows.Count
    $cell = [Math]::Floor($Size / [Math]::Max($gw, $gh))
    # Whole-pixel cells keep the art crisp; if they would leave the art much smaller than the
    # layer (or zero), fall back to a fractional cell whose edges are rounded to whole pixels.
    if ($cell -lt 1 -or ($cell * [Math]::Max($gw, $gh)) -lt (0.75 * $Size)) {
        $cell = $Size / [double][Math]::Max($gw, $gh)
    }
    $offX = ($Size - $cell * $gw) / 2.0
    $offY = ($Size - $cell * $gh) / 2.0
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($y = 0; $y -lt $gh; $y++) {
        $row = $Rows[$y]
        $y0 = [int][Math]::Round($offY + $y * $cell)
        $y1 = [int][Math]::Round($offY + ($y + 1) * $cell)
        for ($x = 0; $x -lt $row.Length; $x++) {
            $c = $Colors[[string]$row[$x]]
            if ($null -eq $c -or $c.A -eq 0) { continue }
            $x0 = [int][Math]::Round($offX + $x * $cell)
            $x1 = [int][Math]::Round($offX + ($x + 1) * $cell)
            for ($py = [Math]::Max(0, $y0); $py -lt [Math]::Min($Size, $y1); $py++) {
                for ($px = [Math]::Max(0, $x0); $px -lt [Math]::Min($Size, $x1); $px++) {
                    $bmp.SetPixel($px, $py, $c)
                }
            }
        }
    }
    return $bmp
}

# --- the source grid -----------------------------------------------------------------------
$src = Read-SpriteSource -Path $SpritesPath
if ($null -ne $src) {
    $rows = [string[]]$src.Rows
    $legendHex = $src.Legend
    Write-Host "Source: IdleOpen from $SpritesPath ($($rows.Count) rows)"
} else {
    $rows = [string[]]$PlaceholderRows
    $legendHex = $PlaceholderLegend
    Write-Host "Source: built-in placeholder grid (no IdleOpen frame in $SpritesPath)"
}

$colors = New-Object System.Collections.Hashtable ([StringComparer]::Ordinal)
foreach ($k in $legendHex.Keys) { $colors[[string]$k] = ConvertTo-Color $legendHex[$k] }
$unknown = New-Object System.Collections.Hashtable ([StringComparer]::Ordinal)
foreach ($r in $rows) { foreach ($ch in $r.ToCharArray()) { if (-not $colors.ContainsKey([string]$ch)) { $unknown[[string]$ch] = $true } } }
if ($unknown.Count -gt 0) {
    Write-Warning ("Characters with no Legend entry, drawn transparent: " + (($unknown.Keys | Sort-Object) -join ' '))
}

# --- render each size to PNG bytes ---------------------------------------------------------
$pngs = @()
foreach ($s in $Sizes) {
    $bmp = New-Layer -Rows $rows -Colors $colors -Size $s
    try {
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , ($ms.ToArray())
        $ms.Dispose()
        if ($PreviewPath -and $s -eq 64) {
            $bmp.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "Preview: $PreviewPath"
        }
    } finally {
        $bmp.Dispose()
    }
}

# --- write the .ico: ICONDIR (6 bytes), n x ICONDIRENTRY (16 bytes), then the PNG blobs ------
$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$fs = [IO.File]::Create($OutPath)
try {
    $w = New-Object IO.BinaryWriter($fs)
    $w.Write([UInt16]0)                 # reserved
    $w.Write([UInt16]1)                 # type 1 = icon
    $w.Write([UInt16]$Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $s = $Sizes[$i]
        $dim = $s
        if ($s -ge 256) { $dim = 0 }    # 0 means 256 in the entry
        $w.Write([byte]$dim)            # width
        $w.Write([byte]$dim)            # height
        $w.Write([byte]0)               # palette colours (none)
        $w.Write([byte]0)               # reserved
        $w.Write([UInt16]1)             # colour planes
        $w.Write([UInt16]32)            # bits per pixel
        $w.Write([UInt32]$pngs[$i].Length)
        $w.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write([byte[]]$p) }
    $w.Flush()
} finally {
    $fs.Dispose()
}

# --- verify -----------------------------------------------------------------------------------
# System.Drawing.Icon checks the directory and picks each size by name. It cannot decode PNG
# layers under .NET Framework (PS 5.1: ToBitmap throws), so the pixels are checked with WIC,
# the decoder Windows and WPF use for the tray, the taskbar and Explorer.
$found = @()
$ico = New-Object System.Drawing.Icon($OutPath)
$ico.Dispose()
foreach ($s in $Sizes) {
    $one = New-Object System.Drawing.Icon($OutPath, $s, $s)
    try { $found += "$($one.Width)x$($one.Height)" } finally { $one.Dispose() }
}
Add-Type -AssemblyName PresentationCore
$decoded = @()
$rs = [IO.File]::OpenRead($OutPath)
try {
    $dec = New-Object System.Windows.Media.Imaging.IconBitmapDecoder($rs,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    foreach ($f in $dec.Frames) { $decoded += "$($f.PixelWidth)x$($f.PixelHeight)" }
} finally {
    $rs.Dispose()
}
if ($decoded.Count -ne $Sizes.Count) {
    throw "The icon decoded to $($decoded.Count) frames, expected $($Sizes.Count)."
}
$bytes = (Get-Item -LiteralPath $OutPath).Length
Write-Host "Icon: $OutPath ($bytes bytes)"
Write-Host ("Layers by System.Drawing.Icon: " + ($found -join ', '))
Write-Host ("Frames decoded by WIC: " + ($decoded -join ', '))
