# Quick Start: Pin to Taskbar

## TL;DR - Just Do This:

1. **Publish** your app (you've already done this)

2. **Run the exe ONCE**:
   ```
   HardwareTemplateBuilder.App.exe
   ```
   Then close it.

3. **Windows Key** ? Type: `Hardware Template Builder`

4. **RIGHT-CLICK** the app ? **Pin to taskbar**

? **Done!**

---

## What Changed in This Fix

The application now **automatically sets the AppUserModelID on the Start Menu shortcut** using Windows COM APIs. This is the critical piece that enables taskbar pinning for single-file published apps.

**Previous issue**: The AppUserModelID was only set on the running process, but Windows needs it on the permanent shortcut file for pinning to work.

**Current solution**: When you run the app, it:
1. Creates a Start Menu shortcut if it doesn't exist
2. Sets the AppUserModelID directly on the shortcut file using `IPropertyStore`
3. Updates the shortcut if you republish to a new location

---

## Still Not Working?

### Option 1: Use the Fix Script
```powershell
cd "path\to\publish\folder"
.\FixTaskbarPinning.ps1
```

### Option 2: Manual Reset
1. Delete: `%AppData%\Microsoft\Windows\Start Menu\Programs\Hardware Template Builder.lnk`
2. Run the app once
3. Pin from Start Menu

---

## Files Changed
- ? `Program.cs` - Added COM interop to set AppUserModelID on shortcut
- ? `app.manifest` - Fixed assembly identity to match AppUserModelID
- ? `HardwareTemplateBuilder.App.csproj` - Added AssemblyName property
- ? Created `FixTaskbarPinning.ps1` - Manual fix script
- ? Updated `VerifyAppId.ps1` - Verification script
- ? Updated `TASKBAR_PINNING_INSTRUCTIONS.md` - Full documentation

---

## Technical Details (For the Curious)

**The Problem**: Single-file .NET apps extract to a temp folder at runtime. Windows can't pin temporary files.

**The Solution**: 
- Create a permanent shortcut in Start Menu
- Set `System.AppUserModel.ID` property on the shortcut using Windows Property Store
- This tells Windows the shortcut represents the app with that AppUserModelID
- When the app runs, it sets the same AppUserModelID via `SetCurrentProcessExplicitAppUserModelID`
- Windows matches the two and enables pinning

**AppUserModelID**: `HardwareTemplateBuilder.App`
- Must match in: app manifest, assembly name, code, and shortcut property
