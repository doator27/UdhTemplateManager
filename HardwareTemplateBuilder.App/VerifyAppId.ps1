# PowerShell script to verify AppUserModelID setup for taskbar pinning
# Run this script from the publish directory after publishing your app

param(
    [string]$ExePath = "HardwareTemplateBuilder.App.exe"
)

Write-Host ""
Write-Host "=== Hardware Template Builder - Pinning Verification ===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $ExePath)) {
    Write-Host "Error: Executable not found at $ExePath" -ForegroundColor Red
    Write-Host "Make sure you run this script from the publish directory or provide the full path to the .exe" -ForegroundColor Yellow
    exit 1
}

# Get the full path
$FullPath = (Resolve-Path $ExePath).Path
Write-Host "? Found executable:" -ForegroundColor Green
Write-Host "  $FullPath" -ForegroundColor Gray
Write-Host ""

# Check if the executable is running
$Process = Get-Process -Name ($ExePath -replace '\.exe$','') -ErrorAction SilentlyContinue
if ($Process) {
    Write-Host "? Warning: The application is currently running." -ForegroundColor Yellow
    Write-Host "  Close it before testing pinning." -ForegroundColor Gray
    Write-Host ""
}

# Check for Start Menu shortcut
$StartMenuFolder = [Environment]::GetFolderPath('Programs')
$ShortcutPath = Join-Path $StartMenuFolder "Hardware Template Builder.lnk"

if (Test-Path $ShortcutPath) {
    Write-Host "? Start Menu shortcut exists:" -ForegroundColor Green
    Write-Host "  $ShortcutPath" -ForegroundColor Gray
    
    # Check if shortcut points to the right exe
    try {
        $WScriptShell = New-Object -ComObject WScript.Shell
        $Shortcut = $WScriptShell.CreateShortcut($ShortcutPath)
        $Target = $Shortcut.TargetPath
        
        if ($Target -eq $FullPath) {
            Write-Host "? Shortcut target is correct" -ForegroundColor Green
        } else {
            Write-Host "? Shortcut points to different location:" -ForegroundColor Yellow
            Write-Host "  Expected: $FullPath" -ForegroundColor Gray
            Write-Host "  Actual:   $Target" -ForegroundColor Gray
            Write-Host "  Run the app once to update the shortcut" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "? Could not verify shortcut target" -ForegroundColor Yellow
    }
} else {
    Write-Host "? Start Menu shortcut NOT found:" -ForegroundColor Red
    Write-Host "  Expected at: $ShortcutPath" -ForegroundColor Gray
    Write-Host ""
    Write-Host "? You MUST run the application once before pinning!" -ForegroundColor Yellow
    Write-Host "  This will create the required Start Menu shortcut." -ForegroundColor Gray
}

Write-Host ""
Write-Host "=== How to Pin to Taskbar ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Step 1: Run the application (if you haven't already)" -ForegroundColor White
Write-Host "  ? Double-click: $FullPath" -ForegroundColor Gray
Write-Host "  ? Then close the application" -ForegroundColor Gray
Write-Host ""
Write-Host "Step 2: Pin from Start Menu (RECOMMENDED)" -ForegroundColor White
Write-Host "  1. Press the Windows key" -ForegroundColor Gray
Write-Host "  2. Type: Hardware Template Builder" -ForegroundColor Gray
Write-Host "  3. RIGHT-CLICK on the app in search results" -ForegroundColor Gray
Write-Host "  4. Select 'Pin to taskbar'" -ForegroundColor Gray
Write-Host ""
Write-Host "Alternative: Pin from Start Menu folder" -ForegroundColor White
Write-Host "  1. Open: $StartMenuFolder" -ForegroundColor Gray
Write-Host "  2. RIGHT-CLICK on 'Hardware Template Builder.lnk'" -ForegroundColor Gray
Write-Host "  3. Select 'Pin to taskbar'" -ForegroundColor Gray
Write-Host ""
Write-Host "Alternative: Pin from running app" -ForegroundColor White
Write-Host "  1. Run the application" -ForegroundColor Gray
Write-Host "  2. RIGHT-CLICK the taskbar icon" -ForegroundColor Gray
Write-Host "  3. Select 'Pin to taskbar'" -ForegroundColor Gray
Write-Host ""
Write-Host "=== Troubleshooting ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "If pinning still doesn't work:" -ForegroundColor Yellow
Write-Host "  1. Delete the shortcut at: $ShortcutPath" -ForegroundColor Gray
Write-Host "  2. Run the app once to recreate it" -ForegroundColor Gray
Write-Host "  3. Try pinning again from Start Menu" -ForegroundColor Gray
Write-Host ""
Write-Host "Or run the fix script:" -ForegroundColor Yellow
Write-Host "  .\FixTaskbarPinning.ps1" -ForegroundColor Gray
Write-Host ""

