# Crop+zoom a PNG region for inspection. ASCII only (PS 5.1 reads BOM-less as ANSI).
# Usage: powershell -File zoom-crop.ps1 -Src a.png -Dst b.png -X 50 -Y 0 -W 350 -H 64 -Scale 3
param(
  [string]$Src = "",
  [string]$Dst = "",
  [int]$X = 0,
  [int]$Y = 0,
  [int]$W = 100,
  [int]$H = 100,
  [int]$Scale = 3
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($Src)
$bmp = New-Object System.Drawing.Bitmap([int]($W * $Scale), [int]($H * $Scale))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$dstRect = New-Object System.Drawing.Rectangle(0, 0, [int]($W * $Scale), [int]($H * $Scale))
$srcRect = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
$g.DrawImage($img, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$bmp.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $img.Dispose()
Write-Output "SAVED $Dst"
