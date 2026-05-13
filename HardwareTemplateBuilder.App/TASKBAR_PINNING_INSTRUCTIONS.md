# Taskbar Pinning Instructions

## What Was Changed

To enable taskbar pinning for the Hardware Template Builder application, the following changes were made:

### 1. Application Manifest (`app.manifest`)
- Set `assemblyIdentity name` to match AppUserModelID: `HardwareTemplateBuilder.App`
- Added `trustInfo` section with proper execution level settings
- Added DPI awareness settings for better display on high-DPI monitors

### 2. Program.cs
- Added Windows API call to set Application User Model ID
- The AppUserModelID is set to: `HardwareTemplateBuilder.App`
- Added automatic Start Menu shortcut creation on first run (required for Windows 10/11 pinning)

### 3. Project File (`HardwareTemplateBuilder.App.csproj`)
- Set `AssemblyName` to `HardwareTemplateBuilder.App` (matches AppUserModelID)
- Added Company, Product, Description, and Copyright metadata
- Added `IncludeAllContentForSelfExtract` to ensure manifest is embedded in single-file publish
- Added `EnableCompressionInSingleFile` for better single-file performance

### 4. Publish Profiles
- All publish profiles already include `IncludeAllContentForSelfExtract=true`

## How to Pin to Taskbar

**IMPORTANT**: For Windows 10/11, you must run the application **at least once** before pinning. This creates the required Start Menu shortcut automatically.

### Step 1: Run the Application Once
1. Navigate to your publish folder (e.g., `T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\Template App New`)
2. Double-click `HardwareTemplateBuilder.App.exe` to run it
3. Close the application

### Step 2: Pin from Start Menu (RECOMMENDED - Most Reliable)
1. Press the Windows key
2. Start typing "Hardware Template Builder"
3. When the app appears in the search results, **RIGHT-CLICK** on it
4. Select **"Pin to taskbar"**

### Alternative Method: Pin from Running Application
1. Run `HardwareTemplateBuilder.App.exe` from the publish folder
2. While the application is running, **RIGHT-CLICK** the application icon in the taskbar
3. Select **"Pin to taskbar"**

### Alternative Method: Pin from File Explorer
1. Navigate to the publish folder in File Explorer
2. **RIGHT-CLICK** on `HardwareTemplateBuilder.App.exe`
3. Select **"Pin to taskbar"** (or "Show more options > Pin to taskbar" on Windows 11)

## Troubleshooting

### "Pin to taskbar" option is grayed out or missing
- **Make sure you've run the app at least once** (this creates the Start Menu shortcut)
- Make sure you're running the published version (not from the Debug folder)
- Try the Start Menu method instead (most reliable)
- Ensure the .exe is not in a temporary location (Windows blocks pinning from temp folders)
- Make sure the file is not blocked: Right-click the .exe > Properties > Unblock (if present)

### The pinned icon doesn't work correctly
- Delete the old pinned icon and re-pin after running the app once
- The Application User Model ID ensures the pinned icon is associated with the correct application
- If you move the .exe to a different location, you'll need to re-pin it

### Single-File Publishing Considerations
The app is configured to publish as a single-file executable. This is fully supported, and the manifest is embedded in the executable to enable taskbar pinning.

## Technical Details

**Application User Model ID**: `HardwareTemplateBuilder.App`
**Assembly Name**: `HardwareTemplateBuilder.App`
**Manifest Assembly Identity**: `HardwareTemplateBuilder.App`

These three identifiers **must match** for taskbar pinning to work correctly with single-file published applications.

The app automatically creates a Start Menu shortcut on first run at:
`%AppData%\Microsoft\Windows\Start Menu\Programs\Hardware Template Builder.lnk`

This shortcut is required for Windows 10/11 to enable the "Pin to taskbar" functionality.
