# Taskbar Pinning Instructions

## What Was Changed

To enable taskbar pinning for the Hardware Template Builder application, the following changes were made:

### 1. Application Manifest (`app.manifest`)
- Added `trustInfo` section with proper execution level settings
- Added DPI awareness settings for better display on high-DPI monitors

### 2. Program.cs
- Added Windows API call to set Application User Model ID
- The AppUserModelID is set to: `HardwareTemplateBuilder.App`

### 3. Project File (`HardwareTemplateBuilder.App.csproj`)
- Added Company, Product, Description, and Copyright metadata
- Added `IncludeAllContentForSelfExtract` to ensure manifest is embedded in single-file publish

### 4. Publish Profiles
- Updated all publish profiles to include `IncludeAllContentForSelfExtract=true`

## How to Pin to Taskbar

After publishing the application, follow these steps:

### Method 1: Pin from Taskbar (Recommended)
1. Navigate to the publish folder
2. Run `HardwareTemplateBuilder.App.exe`
3. While the application is running, **RIGHT-CLICK** the application icon in the taskbar
4. Select **"Pin to taskbar"**

### Method 2: Pin from File Explorer
1. Navigate to the publish folder in File Explorer
2. **RIGHT-CLICK** on `HardwareTemplateBuilder.App.exe`
3. Select **"Pin to taskbar"** (or "Show more options > Pin to taskbar" on Windows 11)

### Method 3: Create a Shortcut First (Workaround)
If the above methods don't work:
1. Navigate to the publish folder in File Explorer
2. **RIGHT-CLICK** on `HardwareTemplateBuilder.App.exe`
3. Select **"Create shortcut"**
4. **RIGHT-CLICK** the newly created shortcut
5. Select **"Pin to taskbar"**

## Troubleshooting

### "Pin to taskbar" option is grayed out or missing
- Make sure you're running the published version (not from the Debug folder)
- Try running the app as administrator once
- Ensure the .exe is not in a temporary location (Windows sometimes blocks pinning from temp folders)
- Make sure the file is not blocked: Right-click the .exe > Properties > Unblock (if present)

### The pinned icon doesn't work correctly
- The Application User Model ID ensures the pinned icon is associated with the correct application
- If you move the .exe to a different location, you'll need to re-pin it

### Single-File Publishing Considerations
The app is configured to publish as a single-file executable. This is fully supported, and the manifest is embedded in the executable to enable taskbar pinning.

## Technical Details

**Application User Model ID**: `HardwareTemplateBuilder.App`

This ID is set programmatically when the application starts and tells Windows how to identify the application for taskbar grouping and pinning purposes.
