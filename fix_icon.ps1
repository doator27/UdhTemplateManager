# Script to fix app icon - remove alpha channel and ensure it fills the area
Add-Type -AssemblyName System.Drawing

$iconPath = "HardwareTemplateBuilder.App\Assets\AppIcon.png"
$icoPath = "HardwareTemplateBuilder.App\Assets\AppIcon.ico"

# Backup the original files
if (Test-Path $iconPath) {
    Copy-Item $iconPath "$iconPath.backup" -Force
    Write-Host "Backed up PNG icon"
}

if (Test-Path $icoPath) {
    Copy-Item $icoPath "$icoPath.backup" -Force
    Write-Host "Backed up ICO icon"
}

# Load the original image
$img = [System.Drawing.Image]::FromFile((Resolve-Path $iconPath).Path)
Write-Host "Original image size: $($img.Width)x$($img.Height)"

# Create a new bitmap without alpha channel (24-bit RGB)
$newBitmap = New-Object System.Drawing.Bitmap($img.Width, $img.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)

# Create graphics object and fill with white background
$graphics = [System.Drawing.Graphics]::FromImage($newBitmap)
$graphics.Clear([System.Drawing.Color]::White)

# Set high quality rendering
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# Draw the original image on top (this will blend alpha with white background)
$graphics.DrawImage($img, 0, 0, $img.Width, $img.Height)

# Clean up source image
$graphics.Dispose()
$img.Dispose()

# Save the new PNG (overwrite original)
$newBitmap.Save((Resolve-Path $iconPath).Path, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Saved new PNG without alpha channel"

# Now create a proper ICO file from the bitmap
# ICO files typically contain multiple sizes, we'll create 256x256, 128x128, 64x64, 48x48, 32x32, 16x16
$sizes = @(256, 128, 64, 48, 32, 16)
$iconImages = @()

foreach ($size in $sizes) {
    $resized = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($resized)
    $g.Clear([System.Drawing.Color]::White)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($newBitmap, 0, 0, $size, $size)
    $g.Dispose()
    $iconImages += $resized
}

# Save as ICO using a memory stream approach
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# ICO header
$bw.Write([UInt16]0)      # Reserved
$bw.Write([UInt16]1)      # Type (1 = ICO)
$bw.Write([UInt16]$iconImages.Count)  # Number of images

# Calculate offset for first image
$offset = 6 + (16 * $iconImages.Count)

# Write directory entries
foreach ($img in $iconImages) {
    $bw.Write([Byte]($img.Width -eq 256 ? 0 : $img.Width))   # Width (0 means 256)
    $bw.Write([Byte]($img.Height -eq 256 ? 0 : $img.Height)) # Height (0 means 256)
    $bw.Write([Byte]0)        # Color palette
    $bw.Write([Byte]0)        # Reserved
    $bw.Write([UInt16]1)      # Color planes
    $bw.Write([UInt16]24)     # Bits per pixel
    
    # We'll write the size and offset after we know them
    $sizePos = $ms.Position
    $bw.Write([UInt32]0)      # Size (placeholder)
    $bw.Write([UInt32]$offset) # Offset
}

# Write image data
foreach ($img in $iconImages) {
    $imgMs = New-Object System.IO.MemoryStream
    $img.Save($imgMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $imgData = $imgMs.ToArray()
    $imgMs.Dispose()
    
    # Update size in directory entry
    $currentPos = $ms.Position
    $entryIndex = $iconImages.IndexOf($img)
    $ms.Seek(6 + (16 * $entryIndex) + 8, [System.IO.SeekOrigin]::Begin) | Out-Null
    $bw.Write([UInt32]$imgData.Length)
    $ms.Seek($currentPos, [System.IO.SeekOrigin]::Begin) | Out-Null
    
    $bw.Write($imgData)
    $offset += $imgData.Length
}

# Save ICO file
[System.IO.File]::WriteAllBytes((Resolve-Path $icoPath).Path, $ms.ToArray())

# Clean up
$bw.Dispose()
$ms.Dispose()
foreach ($img in $iconImages) {
    $img.Dispose()
}
$newBitmap.Dispose()

Write-Host "ICO file created successfully with $($iconImages.Count) sizes"
Write-Host "Icon fix complete!"
