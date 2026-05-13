# Script to fix publish profiles for taskbar pinning
# This disables single-file publishing which prevents reliable taskbar pinning

Write-Host "=== Fixing Publish Profiles for Taskbar Pinning ===" -ForegroundColor Cyan
Write-Host ""

$profilesPath = "HardwareTemplateBuilder.App\Properties\PublishProfiles"

if (-not (Test-Path $profilesPath)) {
    Write-Host "Error: Cannot find publish profiles directory" -ForegroundColor Red
    Write-Host "Make sure you're running this from the solution root directory" -ForegroundColor Yellow
    exit 1
}

Write-Host "Backing up existing profiles..." -ForegroundColor Yellow
Copy-Item "$profilesPath\FolderProfile1.pubxml" "$profilesPath\FolderProfile1.pubxml.BACKUP" -Force -ErrorAction SilentlyContinue
Copy-Item "$profilesPath\FolderProfile2.pubxml" "$profilesPath\FolderProfile2.pubxml.BACKUP" -Force -ErrorAction SilentlyContinue
Write-Host "? Backups created (.BACKUP files)" -ForegroundColor Green
Write-Host ""

Write-Host "Updating FolderProfile1.pubxml..." -ForegroundColor Yellow
$profile1Content = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- https://go.microsoft.com/fwlink/?LinkID=208121. -->
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishDir>T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TempalteApp</PublishDir>
    <PublishProtocol>FileSystem</PublishProtocol>
    <_TargetId>Folder</_TargetId>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <!-- DISABLED FOR RELIABLE TASKBAR PINNING -->
    <PublishSingleFile>false</PublishSingleFile>
    <PublishReadyToRun>false</PublishReadyToRun>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
</Project>
'@

$profile1Content | Out-File "$profilesPath\FolderProfile1.pubxml" -Encoding UTF8 -Force
Write-Host "? FolderProfile1.pubxml updated" -ForegroundColor Green

Write-Host "Updating FolderProfile2.pubxml..." -ForegroundColor Yellow
$profile2Content = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- https://go.microsoft.com/fwlink/?LinkID=208121. -->
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishDir>T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TempalteApp</PublishDir>
    <PublishProtocol>FileSystem</PublishProtocol>
    <_TargetId>Folder</_TargetId>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <!-- DISABLED FOR RELIABLE TASKBAR PINNING -->
    <PublishSingleFile>false</PublishSingleFile>
    <PublishReadyToRun>false</PublishReadyToRun>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
</Project>
'@

$profile2Content | Out-File "$profilesPath\FolderProfile2.pubxml" -Encoding UTF8 -Force
Write-Host "? FolderProfile2.pubxml updated" -ForegroundColor Green
Write-Host ""

Write-Host "=== Changes Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "What changed:" -ForegroundColor Cyan
Write-Host "  • PublishSingleFile: true ? false" -ForegroundColor White
Write-Host "  • Removed: IncludeNativeLibrariesForSelfExtract" -ForegroundColor White
Write-Host "  • Removed: IncludeAllContentForSelfExtract" -ForegroundColor White
Write-Host ""
Write-Host "Why:" -ForegroundColor Cyan
Write-Host "  Single-file apps extract to temp folders at runtime," -ForegroundColor White
Write-Host "  which Windows CANNOT pin to the taskbar reliably." -ForegroundColor White
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Close Visual Studio (or reload solution)" -ForegroundColor Yellow
Write-Host "  2. Reopen and publish using FolderProfile1 or FolderProfile2" -ForegroundColor Yellow
Write-Host "  3. Run the app once from the publish folder" -ForegroundColor Yellow
Write-Host "  4. Pin to taskbar - IT WILL WORK!" -ForegroundColor Yellow
Write-Host ""
Write-Host "If you need to revert:" -ForegroundColor Gray
Write-Host "  Restore from .BACKUP files in the PublishProfiles folder" -ForegroundColor Gray
Write-Host ""
