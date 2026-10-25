# Makes downscaled copies of samples to test other resolutions.
param([int]$height = 1080)
Add-Type -AssemblyName System.Drawing
foreach ($f in Get-ChildItem "$PSScriptRoot\..\samples\*.png") {
  $s = [System.Drawing.Bitmap]::FromFile($f.FullName)
  $w = [int]($s.Width * $height / $s.Height)
  $d = New-Object System.Drawing.Bitmap $w, $height
  $g = [System.Drawing.Graphics]::FromImage($d)
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.DrawImage($s, 0, 0, $w, $height)
  $out = Join-Path $env:TEMP ($f.BaseName + "_$height.png")
  $d.Save($out); $out
  $g.Dispose(); $d.Dispose(); $s.Dispose()
}
