# Generates the app icon and the profile placeholder into ..\src\Assets.
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# The chat wallpapers (Wallpaper.Light.png / Wallpaper.Dark.png) are WhatsApp's doodle tiles,
# checked in as-is and repeated by Controls\TiledBackground; nothing to generate for them.

# App icon: green circle with a white chat glyph.
function New-IconBitmap([int] $s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 211, 102))), 0, 0, $s - 1, $s - 1)
    $fs = $s * 0.52
    $font = New-Object System.Drawing.Font 'Segoe Fluent Icons', $fs, ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString([string][char]0xE8BD, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.03), $s, $s), $fmt)
    $g.Dispose()
    return $bmp
}

# Profile placeholder for the rail: grey circle with a person glyph.
$pp = New-Object System.Drawing.Bitmap 96, 96
$g = [System.Drawing.Graphics]::FromImage($pp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(134, 150, 160))), 0, 0, 95, 95)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
$font = New-Object System.Drawing.Font 'Segoe Fluent Icons', 48, ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString([string][char]0xE77B, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 2, 96, 96), $fmt)
$g.Dispose()
$pp.Save((Join-Path $assets 'ProfilePlaceholder.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$pp.Dispose()

$png = New-IconBitmap 256
$png.Save((Join-Path $assets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Multi-size .ico (PNG-compressed entries).
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { $b = New-IconBitmap $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $out.ToArray())
Write-Host "Assets written to $assets"


