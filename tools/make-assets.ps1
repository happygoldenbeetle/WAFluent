# Generates the chat wallpaper (light + dark) and the app icon into ..\src\Assets.
# Doodles are Segoe Fluent Icons glyphs scattered on a jittered grid.
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$glyphs = 0xE722, 0xEB51, 0xE76E, 0xE8D6, 0xE717, 0xE715, 0xE774, 0xE7FC, 0xE709, 0xE8E1,
          0xE718, 0xE80F, 0xEA80, 0xE823, 0xE787, 0xE806, 0xE804, 0xE719, 0xE720, 0xE7F6,
          0xE714, 0xE81D, 0xE734, 0xE7C1, 0xEBE8, 0xE753, 0xE706, 0xE8BD, 0xE7BE, 0xE943,
          0xE8B8, 0xE7F4, 0xE790, 0xE8EC, 0xE724, 0xE8F1, 0xE77B, 0xE913

function New-Wallpaper([string] $path, [System.Drawing.Color] $bg, [System.Drawing.Color] $ink) {
    $w = 2560; $h = 1600; $cell = 64
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear($bg)
    $brush = New-Object System.Drawing.SolidBrush $ink
    $rand = New-Object System.Random 7
    for ($y = 0; $y -lt $h + $cell; $y += $cell) {
        for ($x = 0; $x -lt $w + $cell; $x += $cell) {
            $size = 18 + $rand.Next(10)
            $font = New-Object System.Drawing.Font 'Segoe Fluent Icons', $size, ([System.Drawing.GraphicsUnit]::Pixel)
            $glyph = [char] $glyphs[$rand.Next($glyphs.Count)]
            $state = $g.Save()
            $g.TranslateTransform($x + $rand.Next(-18, 18), $y + $rand.Next(-18, 18))
            $g.RotateTransform($rand.Next(-35, 35))
            $g.DrawString([string] $glyph, $font, $brush, -($size / 2), -($size / 2))
            $g.Restore($state)
            $font.Dispose()
        }
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

New-Wallpaper (Join-Path $assets 'Wallpaper.Light.png') ([System.Drawing.Color]::FromArgb(240, 239, 237)) ([System.Drawing.Color]::FromArgb(40, 110, 110, 110))
New-Wallpaper (Join-Path $assets 'Wallpaper.Dark.png')  ([System.Drawing.Color]::FromArgb(28, 28, 28))    ([System.Drawing.Color]::FromArgb(13, 255, 255, 255))

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


