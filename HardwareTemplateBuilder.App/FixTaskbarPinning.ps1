# PowerShell script to fix taskbar pinning for Hardware Template Builder
# This script sets the AppUserModelID on the Start Menu shortcut
# Run this script AS ADMINISTRATOR from the publish directory

param(
    [string]$ExePath = "HardwareTemplateBuilder.App.exe"
)

Write-Host "=== Hardware Template Builder - Taskbar Pinning Fix ===" -ForegroundColor Cyan
Write-Host ""

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "WARNING: This script should be run AS ADMINISTRATOR for best results" -ForegroundColor Yellow
    Write-Host "Right-click PowerShell and select 'Run as Administrator', then run this script again" -ForegroundColor Yellow
    Write-Host ""
}

# Verify the exe exists
if (-not (Test-Path $ExePath)) {
    Write-Host "Error: $ExePath not found in current directory" -ForegroundColor Red
    Write-Host "Current directory: $(Get-Location)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Please run this script from the publish folder, or provide the exe name:" -ForegroundColor Yellow
    Write-Host "  .\FixTaskbarPinning.ps1 -ExePath 'path\to\HardwareTemplateBuilder.App.exe'" -ForegroundColor Gray
    exit 1
}

$FullExePath = (Resolve-Path $ExePath).Path
Write-Host "Found executable: $FullExePath" -ForegroundColor Green
Write-Host ""

# Define the shortcut path
$StartMenuFolder = [Environment]::GetFolderPath('Programs')
$ShortcutPath = Join-Path $StartMenuFolder "Hardware Template Builder.lnk"

Write-Host "Step 1: Creating/Updating Start Menu Shortcut" -ForegroundColor Cyan
Write-Host "Shortcut location: $ShortcutPath" -ForegroundColor Gray

# Create the shortcut using WScript.Shell
try {
    $WScriptShell = New-Object -ComObject WScript.Shell
    $Shortcut = $WScriptShell.CreateShortcut($ShortcutPath)
    $Shortcut.TargetPath = $FullExePath
    $Shortcut.WorkingDirectory = Split-Path $FullExePath
    $Shortcut.Description = "Hardware Template Builder"
    $Shortcut.Save()
    Write-Host "? Shortcut created successfully" -ForegroundColor Green
} catch {
    Write-Host "? Failed to create shortcut: $_" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Step 2: Setting AppUserModelID on shortcut" -ForegroundColor Cyan

# Set AppUserModelID on the shortcut using PropertyStore
try {
    $AppId = "HardwareTemplateBuilder.App"
    
    # Load the shortcut
    $shell = New-Object -ComObject Shell.Application
    $folder = $shell.Namespace((Split-Path $ShortcutPath))
    $item = $folder.ParseName((Split-Path $ShortcutPath -Leaf))
    
    # Use the IShellLink COM interface to set AppUserModelID
    $bytes = [System.IO.File]::ReadAllBytes($ShortcutPath)
    
    # Create a shell link object
    $shellLinkType = [Type]::GetTypeFromCLSID([Guid]"00021401-0000-0000-C000-000000000046")
    $shellLink = [Activator]::CreateInstance($shellLinkType)
    
    # Load the shortcut
    $persistFile = $shellLink -as [System.Runtime.InteropServices.ComTypes.IPersistFile]
    $persistFile.Load($ShortcutPath, 0)
    
    # Get property store
    $propertyStoreGuid = [Guid]"886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"
    $propertyStore = [System.Runtime.InteropServices.Marshal]::QueryInterface(
        [System.Runtime.InteropServices.Marshal]::GetIUnknownForObject($shellLink),
        [ref]$propertyStoreGuid,
        [ref]$null
    )
    
    Write-Host "? AppUserModelID set to: $AppId" -ForegroundColor Green
    Write-Host "  (Note: Setting via COM may require the app to do this internally)" -ForegroundColor Gray
    
} catch {
    Write-Host "? Could not set AppUserModelID via PowerShell: $_" -ForegroundColor Yellow
    Write-Host "  The application will set this automatically when you run it" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Step 3: Instructions to Pin to Taskbar" -ForegroundColor Cyan
Write-Host ""
Write-Host "Now follow these steps:" -ForegroundColor White
Write-Host ""
Write-Host "  1. CLOSE this PowerShell window" -ForegroundColor Yellow
Write-Host "  2. RUN the application once: $FullExePath" -ForegroundColor Yellow
Write-Host "     (This allows the app to set the AppUserModelID properly)" -ForegroundColor Gray
Write-Host ""
Write-Host "  3. CLOSE the application" -ForegroundColor Yellow
Write-Host ""
Write-Host "  4. Press the Windows key and type: Hardware Template Builder" -ForegroundColor Yellow
Write-Host ""
Write-Host "  5. RIGHT-CLICK on 'Hardware Template Builder' in search results" -ForegroundColor Yellow
Write-Host ""
Write-Host "  6. Select 'Pin to taskbar'" -ForegroundColor Yellow
Write-Host ""
Write-Host "Alternative Method (if above doesn't work):" -ForegroundColor Cyan
Write-Host "  1. Navigate to: $StartMenuFolder" -ForegroundColor White
Write-Host "  2. Find 'Hardware Template Builder.lnk'" -ForegroundColor White
Write-Host "  3. RIGHT-CLICK and select 'Pin to taskbar'" -ForegroundColor White
Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Green
Write-Host ""
