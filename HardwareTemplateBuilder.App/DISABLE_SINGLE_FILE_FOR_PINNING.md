# CRITICAL FIX: Disable Single-File Publishing

## The Real Problem

**Single-file .NET applications CANNOT be reliably pinned to the Windows taskbar.**

Why? Because single-file apps:
1. Extract to a temporary folder when run (`%TEMP%\...`)
2. Windows won't pin files from temp locations
3. Shortcuts pointing to the .exe will break when the temp folder is cleaned
4. The AppUserModelID workarounds are unreliable with single-file apps

## The Solution: Regular Self-Contained Publishing

Publish as a **regular self-contained app** (not single-file). This creates a folder with your .exe and all dependencies, which Windows can pin reliably.

---

## Step 1: Update Your Publish Profiles

### Close the publish profile files in Visual Studio

Then replace them with these versions:

### FolderProfile1.pubxml (RECOMMENDED)
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishDir>T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TemplateApp</PublishDir>
    <PublishProtocol>FileSystem</PublishProtocol>
    <_TargetId>Folder</_TargetId>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <!-- DISABLED FOR TASKBAR PINNING -->
    <PublishSingleFile>false</PublishSingleFile>
    <PublishReadyToRun>false</PublishReadyToRun>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
</Project>
```

### FolderProfile2.pubxml
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishDir>T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TemplateApp</PublishDir>
    <PublishProtocol>FileSystem</PublishProtocol>
    <_TargetId>Folder</_TargetId>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <!-- DISABLED FOR TASKBAR PINNING -->
    <PublishSingleFile>false</PublishSingleFile>
    <PublishReadyToRun>false</PublishReadyToRun>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
</Project>
```

---

## Step 2: Republish

1. **Close Visual Studio** (or at least close the publish profile files)
2. **Replace** the .pubxml files with the versions above
3. **Reopen Visual Studio**
4. **Right-click** on HardwareTemplateBuilder.App project
5. **Select** "Publish..."
6. **Choose** FolderProfile1
7. **Click** "Publish"

---

## Step 3: Pin to Taskbar (Will Work Now!)

After publishing:

1. **Navigate** to: `T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TemplateApp`
2. **Double-click** `HardwareTemplateBuilder.App.exe` to run it once
3. **Close** the app
4. **Windows Key** ? Type: **Hardware Template Builder**
5. **RIGHT-CLICK** ? **Pin to taskbar**

? **IT WILL WORK!**

---

## What You'll See After Publishing

Instead of one file:
```
TemplateApp/
  ??? HardwareTemplateBuilder.App.exe (single file, ~200MB)
```

You'll see a folder with multiple files:
```
TemplateApp/
  ??? HardwareTemplateBuilder.App.exe (~5MB)
  ??? HardwareTemplateBuilder.App.dll
  ??? Avalonia.dll
  ??? ... (other DLLs)
  ??? [other dependencies]
```

**This is NORMAL and CORRECT!** The .exe is a real permanent file that Windows can pin.

---

## Why This is Better

### Advantages of Regular Publishing:
- ? **Taskbar pinning works perfectly**
- ? **Faster startup** (no extraction needed)
- ? **No temp folder pollution**
- ? **More reliable** - no extraction failures
- ? **Better compatibility** with antivirus software

### Disadvantages:
- ? Multiple files instead of one
- ? Slightly larger total size (but individual exe is smaller)

---

## Alternative: Keep Single-File BUT Create a Permanent Shortcut

If you **absolutely must use single-file**, here's a workaround:

1. Publish as single-file
2. Create a permanent shortcut manually:
   - Right-click the .exe ? "Create Shortcut"
   - Move shortcut to: `%AppData%\Microsoft\Windows\Start Menu\Programs\`
   - Rename to: `Hardware Template Builder.lnk`
3. Run the app once (so it sets AppUserModelID)
4. Pin the shortcut from Start Menu

?? **But this is less reliable than just using regular publishing!**

---

## Recommended Action

**Just disable single-file publishing.** The benefits of taskbar pinning far outweigh having multiple files in the publish folder.

Users don't care about the number of files in the install folder - they just want to pin the app to their taskbar and have it work reliably!
