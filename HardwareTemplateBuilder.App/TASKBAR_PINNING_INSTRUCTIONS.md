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
- **CRITICAL FIX**: Added COM interop to set AppUserModelID directly on the shortcut file
- The shortcut is now recreated on each run if the exe path changes (handles republishing)

### 3. Project File (`HardwareTemplateBuilder.App.csproj`)
- Set `AssemblyName` to `HardwareTemplateBuilder.App` (matches AppUserModelID)
- Added Company, Product, Description, and Copyright metadata
- Added `IncludeAllContentForSelfExtract` to ensure manifest is embedded in single-file publish
- Added `EnableCompressionInSingleFile` for better single-file performance

### 4. Publish Profiles
- All publish profiles already include `IncludeAllContentForSelfExtract=true`

## How to Pin to Taskbar

### Method 1: Automatic (RECOMMENDED - Most Reliable)

1. **Publish** your application to your target folder
2. **Navigate** to the publish folder
3. **Run** `HardwareTemplateBuilder.App.exe` **ONE TIME**
   - The app will automatically create a Start Menu shortcut with the correct AppUserModelID
   - Close the application
4. **Open Start Menu** (press Windows key)
5. **Type** "Hardware Template Builder"
6. **RIGHT-CLICK** on the app in search results
7. **Select** "Pin to taskbar"

✅ **This method should work 100% of the time**

### Method 2: Using the Fix Script (If Method 1 Fails)

1. **Navigate** to the publish folder in PowerShell
2. **Run** (as Administrator):
   ```powershell
   .\FixTaskbarPinning.ps1
   ```
3. **Follow** the on-screen instructions

### Method 3: Manual Shortcut Method (Last Resort)

If the above methods fail:

1. **Navigate** to: `%AppData%\Microsoft\Windows\Start Menu\Programs`
2. **Verify** that `Hardware Template Builder.lnk` exists
3. **RIGHT-CLICK** on the shortcut
4. **Select** "Pin to taskbar"

If the shortcut doesn't exist, run the app once first.

## Troubleshooting

### "Pin to taskbar" option is grayed out or missing

**Most Common Solution:**
1. Delete any existing Start Menu shortcut at: `%AppData%\Microsoft\Windows\Start Menu\Programs\Hardware Template Builder.lnk`
2. Run the published app **once**
3. Close the app
4. Try pinning again using Method 1

**Other Solutions:**
- Make sure you're using the **published** version (not from the Debug folder)
- Ensure the .exe is not in a temporary location (Windows blocks pinning from temp folders)
- Check if the file is blocked: Right-click the .exe > Properties > Unblock (if present)
- Try running the app as Administrator once

### The pinned icon shows the wrong app or doesn't work

1. **Unpin** the current taskbar icon
2. **Delete** the Start Menu shortcut: `%AppData%\Microsoft\Windows\Start Menu\Programs\Hardware Template Builder.lnk`
3. **Run** the app once to recreate the shortcut
4. **Pin** again using Method 1

### After republishing to a new location, pinning stops working

The app now automatically detects when the exe path changes and recreates the shortcut. Just:
1. Run the app once from the new location
2. Pin using Method 1

### Single-File Publishing Considerations

The app is configured to publish as a single-file executable. The critical change that enables taskbar pinning is:

**The AppUserModelID is now set directly on the Start Menu shortcut file itself** using Windows Property Store COM APIs. This is required because:
- Single-file apps extract to a temp folder at runtime
- Windows needs the AppUserModelID to be associated with the permanent shortcut, not the temporary extracted exe
- Setting it only in code (`SetCurrentProcessExplicitAppUserModelID`) is insufficient for pinning

## Technical Details

**Application User Model ID**: `HardwareTemplateBuilder.App`  
**Assembly Name**: `HardwareTemplateBuilder.App`  
**Manifest Assembly Identity**: `HardwareTemplateBuilder.App`

These three identifiers **must match** for taskbar pinning to work correctly.

**Start Menu Shortcut Location**:  
`%AppData%\Microsoft\Windows\Start Menu\Programs\Hardware Template Builder.lnk`

**AppUserModelID is set in two places:**
1. On the running process via `SetCurrentProcessExplicitAppUserModelID` (for taskbar grouping)
2. On the shortcut file via `IPropertyStore` COM interface (for taskbar pinning)

This dual approach ensures both taskbar grouping and pinning work correctly for single-file published applications.
