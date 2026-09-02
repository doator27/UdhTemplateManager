using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HardwareTemplateBuilder.App.Helpers;

namespace HardwareTemplateBuilder.App;

class Program
{
    // P/Invoke for setting Application User Model ID (required for taskbar grouping on Windows)
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLogger.RegisterGlobalHandlers();

        if (OperatingSystem.IsWindows())
        {
            SetCurrentProcessExplicitAppUserModelID("HardwareTemplateBuilder.App");
            CreateStartMenuShortcutIfNeeded();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, isTerminating: true);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Creates a Start Menu shortcut for this application on first run so that
    /// Windows 10/11 exposes the "Pin to taskbar" option when right-clicking it.
    /// Uses the WScript.Shell COM object — no extra packages required.
    /// Safe to call on every launch; recreates the shortcut if needed to ensure AppUserModelID is set.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CreateStartMenuShortcutIfNeeded()
    {
        try
        {
            var programsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var shortcutPath = Path.Combine(programsFolder, "Hardware Template Builder.lnk");

            // Environment.ProcessPath returns the original launcher exe path, not the
            // temp extraction path used by single-file self-contained publish.
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // For single-file apps, we need to ensure the shortcut is always correct
            // Delete and recreate if the target changed (e.g., after republishing to a new location)
            if (File.Exists(shortcutPath))
            {
                try
                {
                    var wshTypeCheck = Type.GetTypeFromProgID("WScript.Shell");
                    if (wshTypeCheck is not null)
                    {
                        dynamic wshCheck = Activator.CreateInstance(wshTypeCheck)!;
                        dynamic existingShortcut = wshCheck.CreateShortcut(shortcutPath);
                        string existingTarget = existingShortcut.TargetPath;
                        string existingIcon = existingShortcut.IconLocation ?? string.Empty;
                        string expectedIcon = exePath + ",0";

                        // If target and icon both match, shortcut is up to date
                        if (string.Equals(existingTarget, exePath, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(existingIcon, expectedIcon, StringComparison.OrdinalIgnoreCase))
                            return;
                        
                        // Otherwise delete and recreate
                        File.Delete(shortcutPath);
                    }
                }
                catch
                {
                    // If we can't check, try to delete and recreate
                    try { File.Delete(shortcutPath); } catch { }
                }
            }

            // Create the shortcut
            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType is null) return;

            dynamic wsh = Activator.CreateInstance(wshType)!;
            dynamic shortcut = wsh.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            shortcut.Description = "Hardware Template Builder";
            shortcut.IconLocation = exePath + ",0";
            shortcut.Save();

            // Set AppUserModelID on the shortcut file itself using PropertyStore
            // This is critical for single-file published apps to pin correctly
            SetShortcutAppUserModelId(shortcutPath, "HardwareTemplateBuilder.App");
        }
        catch
        {
            // Shortcut creation is best-effort; a failure must never crash the app.
        }
    }

    /// <summary>
    /// Sets the AppUserModelID on a shortcut file (.lnk) using Windows Property Store.
    /// This is required for taskbar pinning of single-file published apps.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void SetShortcutAppUserModelId(string shortcutPath, string appId)
    {
        try
        {
            IShellLinkW? shellLink = null;
            IPropertyStore? propStore = null;

            try
            {
                shellLink = (IShellLinkW)new CShellLink();
                ((IPersistFile)shellLink).Load(shortcutPath, 0);
                
                propStore = (IPropertyStore)shellLink;
                
                var pkey = new PropertyKey(
                    new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
                
                var pv = new PropVariant(appId);
                propStore.SetValue(ref pkey, ref pv);
                propStore.Commit();
                
                ((IPersistFile)shellLink).Save(shortcutPath, true);
            }
            finally
            {
                if (propStore != null) Marshal.ReleaseComObject(propStore);
                if (shellLink != null) Marshal.ReleaseComObject(shellLink);
            }
        }
        catch
        {
            // Setting AppUserModelID is best-effort
        }
    }

    #region COM Interop for Shortcut AppUserModelID
    
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder ppszFileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey pkey, out PropVariant pv);
        void SetValue(ref PropertyKey pkey, ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public PropertyKey(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;

        public PropVariant(string value)
        {
            vt = 31; // VT_LPWSTR
            pwszVal = Marshal.StringToCoTaskMemUni(value);
        }
    }

    #endregion
}
