# Covers three of the four corners of the craft slot, mimicking the cursor and the item tooltip.
param([string]$src, [string]$dst)
Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile($src)
$g = [System.Drawing.Graphics]::FromImage($b)
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 190, 170))
$g.FillRectangle($brush, 300, 380, 300, 230)  # tooltip over both top corners
$g.FillRectangle($brush, 470, 680, 60, 40)    # cursor over the bottom-right corner
$g.Dispose(); $b.Save($dst); $b.Dispose()
