# PowerShell script to verify and set AppUserModelID for taskbar pinning
# Run this script from the publish directory after publishing your app

param(
    [string]$ExePath = "HardwareTemplateBuilder.App.exe"
)

Write-Host "Verifying AppUserModelID for: $ExePath" -ForegroundColor Cyan

if (-not (Test-Path $ExePath)) {
    Write-Host "Error: Executable not found at $ExePath" -ForegroundColor Red
    Write-Host "Make sure you run this script from the publish directory or provide the full path to the .exe" -ForegroundColor Yellow
    exit 1
}

# Get the full path
$FullPath = (Resolve-Path $ExePath).Path
Write-Host "Full path: $FullPath" -ForegroundColor Gray

# Check if the executable is running
$Process = Get-Process -Name ($ExePath -replace '\.exe$','') -ErrorAction SilentlyContinue
if ($Process) {
    Write-Host "Warning: The application is currently running. Close it before testing pinning." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "To enable taskbar pinning:" -ForegroundColor Green
Write-Host "1. Run the application at least once from: $FullPath" -ForegroundColor White
Write-Host "2. While it's running, RIGHT-CLICK the taskbar icon" -ForegroundColor White
Write-Host "3. Select 'Pin to taskbar'" -ForegroundColor White
Write-Host ""
Write-Host "Alternative method:" -ForegroundColor Green
Write-Host "1. In File Explorer, navigate to the publish folder" -ForegroundColor White
Write-Host "2. RIGHT-CLICK on $ExePath" -ForegroundColor White
Write-Host "3. Select 'Pin to taskbar' or 'Show more options > Pin to taskbar'" -ForegroundColor White
Write-Host ""
Write-Host "If pinning still doesn't work:" -ForegroundColor Yellow
Write-Host "- Create a shortcut to the .exe" -ForegroundColor White
Write-Host "- Right-click the shortcut and select 'Pin to taskbar'" -ForegroundColor White
Write-Host "- The shortcut will be pinned and will launch your app correctly" -ForegroundColor White
