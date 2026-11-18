using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace HardwareTemplateBuilder.Core.Platform;

/// <summary>
/// Windows shortcut (.lnk) helpers that keep both apps pinnable to the taskbar.
/// <para>
/// The apps run without an AppUserModelID, so Windows identifies them by their exe path. Older
/// versions ran with an explicit ID ("HardwareTemplateBuilder.App" / ".Wpf"), and shortcuts made
/// while they ran (a taskbar pin, a desktop shortcut, a Start Menu entry) carry that ID. When such
/// a shortcut points at the same exe, Windows matches the running window to it, finds an ID the
/// process doesn't have, and the taskbar menu shows only "Close window" (no name, no "Pin to
/// taskbar"). <see cref="StripOldAppIds"/> removes those IDs at startup; <see cref="CreateShortcut"/>
/// writes the plain Start Menu shortcut.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppShortcuts
{
    /// <summary>The IDs older versions of the apps used.</summary>
    public static readonly IReadOnlyList<string> OldAppUserModelIds = new[] { "HardwareTemplateBuilder.App", "HardwareTemplateBuilder.Wpf" };

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid AppUserModelFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PidAppUserModelId = 5;
    private const int StgmReadWrite = 2;
    private const ushort VtEmpty = 0;
    private const ushort VtLpwstr = 31;

    /// <summary>
    /// The folders whose shortcuts are checked: the user's Start Menu programs (with subfolders),
    /// the desktop, and the taskbar's pinned and implicit shortcuts.
    /// </summary>
    public static IEnumerable<string> ShortcutFolders()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        yield return Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "ImplicitAppShortcuts");
    }

    /// <summary>
    /// Removes the AppUserModelID from shortcuts that carry one of the old IDs, or any ID while
    /// pointing at <paramref name="exePath"/>. Returns the shortcuts changed. Best-effort: a
    /// shortcut that can't be read or saved is skipped.
    /// </summary>
    /// <param name="exePath">The running exe.</param>
    /// <param name="folders">Folders to search (default <see cref="ShortcutFolders"/>).</param>
    public static List<string> StripOldAppIds(string exePath, IEnumerable<string>? folders = null)
    {
        var changed = new List<string>();
        foreach (var folder in folders ?? ShortcutFolders())
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var lnk in files)
            {
                try
                {
                    var (target, id) = Read(lnk);
                    if (string.IsNullOrEmpty(id)) continue;
                    bool ours = OldAppUserModelIds.Contains(id, StringComparer.OrdinalIgnoreCase) ||
                                (target != null && SamePath(target, exePath));
                    if (!ours) continue;
                    SetAppId(lnk, null);
                    changed.Add(lnk);
                }
                catch
                {
                    // Not a readable shortcut, or no permission: leave it.
                }
            }
        }
        return changed;
    }

    /// <summary>Writes a shortcut to <paramref name="targetPath"/> (replacing any file there).</summary>
    /// <param name="shortcutPath">The .lnk to write.</param>
    /// <param name="targetPath">The exe it opens.</param>
    /// <param name="description">Its description (tooltip).</param>
    /// <param name="appUserModelId">An AppUserModelID to tag it with; null = none (the apps' normal case).</param>
    public static void CreateShortcut(string shortcutPath, string targetPath, string description, string? appUserModelId = null)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            if (appUserModelId != null) SetId((IPropertyStore)link, appUserModelId);
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Reads a shortcut's target and AppUserModelID (null when it has none).</summary>
    /// <param name="shortcutPath">The .lnk.</param>
    public static (string? Target, string? AppUserModelId) Read(string shortcutPath)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var target = sb.Length > 0 ? Environment.ExpandEnvironmentVariables(sb.ToString()) : null;

            var store = (IPropertyStore)link;
            var key = new PropertyKey { FormatId = AppUserModelFormatId, PropertyId = PidAppUserModelId };
            store.GetValue(ref key, out var value);
            try
            {
                string? id = value.VarType == VtLpwstr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
                return (target, string.IsNullOrEmpty(id) ? null : id);
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Sets or (null) removes a shortcut's AppUserModelID and saves it.</summary>
    /// <param name="shortcutPath">The .lnk.</param>
    /// <param name="appUserModelId">The ID, or null to remove it.</param>
    public static void SetAppId(string shortcutPath, string? appUserModelId)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;
        try
        {
            ((IPersistFile)link).Load(shortcutPath, StgmReadWrite);
            SetId((IPropertyStore)link, appUserModelId);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static void SetId(IPropertyStore store, string? id)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormatId, PropertyId = PidAppUserModelId };
        var value = new PropVariant { VarType = id == null ? VtEmpty : VtLpwstr, Pointer = id == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(id) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            if (value.Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(value.Pointer);
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
