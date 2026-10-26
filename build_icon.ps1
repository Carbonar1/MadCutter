param(
    [Parameter(Mandatory=$true)][string]$Src,
    [Parameter(Mandatory=$true)][string]$OutDir
)

Add-Type -AssemblyName System.Drawing

$outPng = Join-Path $OutDir "app_icon_256.png"
$outIco = Join-Path $OutDir "app.ico"

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::Transparent)

$yellow = [System.Drawing.Color]::FromArgb(255, 255, 210, 0)
$pen = New-Object System.Drawing.Pen($yellow, 22)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$g.DrawLine($pen, 30, 40, 30, $size - 40)
$g.DrawLine($pen, 30, 40, 70, 40)
$g.DrawLine($pen, 30, $size - 40, 70, $size - 40)

$g.DrawLine($pen, $size - 30, 40, $size - 30, $size - 40)
$g.DrawLine($pen, $size - 30, 40, $size - 70, 40)
$g.DrawLine($pen, $size - 30, $size - 40, $size - 70, $size - 40)

$char = [System.Drawing.Image]::FromFile($Src)
$charW = 180
$ratio = $char.Height / $char.Width
$charH = [int]($charW * $ratio)
$cx = ($size - $charW) / 2
$cy = ($size - $charH) / 2
$g.DrawImage($char, [int]$cx, [int]$cy, $charW, $charH)

$g.Dispose()
$char.Dispose()
$bmp.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16, 32, 48, 64, 128, 256)
$pngBytes = @{}
foreach ($s in $sizes) {
    $resized = New-Object System.Drawing.Bitmap($s, $s)
    $rg = [System.Drawing.Graphics]::FromImage($resized)
    $rg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $rg.Clear([System.Drawing.Color]::Transparent)
    $rg.DrawImage($bmp, 0, 0, $s, $s)
    $rg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes[$s] = $ms.ToArray()
    $resized.Dispose()
    $ms.Dispose()
}
$bmp.Dispose()

$fs = [System.IO.File]::Create($outIco)
$bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngBytes[$s]
    $w = if ($s -ge 256) { 0 } else { $s }
    $h = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$w)
    $bw.Write([Byte]$h)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) {
    $bw.Write($pngBytes[$s])
}
$bw.Flush()
$fs.Close()

Write-Output "DONE ICO: $outIco"
Write-Output "Preview PNG: $outPng"
