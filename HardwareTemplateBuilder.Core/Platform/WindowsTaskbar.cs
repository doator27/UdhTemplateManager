using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HardwareTemplateBuilder.Core.Platform;

/// <summary>
/// Makes a window pinnable to the Windows taskbar.
/// <para>
/// Both apps call <c>SetCurrentProcessExplicitAppUserModelID</c> at startup. Once a process has
/// an explicit AppUserModelID, Windows only offers "Pin to taskbar" (and shows the app name in
/// the taskbar right-click menu) if it can find out how to relaunch it, normally from a Start
/// Menu shortcut carrying the same ID. If that shortcut is missing, stale or untagged, the menu
/// shows only "Close window". Setting the relaunch properties on the window itself removes that
/// dependency: the taskbar reads the ID, launch command, display name and icon straight from
/// the window.
/// </para>
/// </summary>
public static class WindowsTaskbar
{
    // PKEY_AppUserModel_* (propkey.h); all share this format ID.
    private static readonly Guid AppUserModelFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PidRelaunchCommand = 2;
    private const uint PidRelaunchIconResource = 3;
    private const uint PidRelaunchDisplayNameResource = 4;
    private const uint PidId = 5;

    /// <summary>
    /// Sets the taskbar identity and relaunch information on a top-level window. Call it as soon
    /// as the window has a native handle (ideally before it is first shown). Does nothing on
    /// other operating systems; never throws (pinning support is best-effort).
    /// </summary>
    /// <param name="hwnd">The window's native handle.</param>
    /// <param name="appUserModelId">Must match the ID passed to <c>SetCurrentProcessExplicitAppUserModelID</c>.</param>
    /// <param name="displayName">The name shown in the taskbar menu and for the pinned item.</param>
    /// <returns>True if the properties were set.</returns>
    public static bool SetRelaunchInfo(IntPtr hwnd, string appUserModelId, string displayName)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return false;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return false;

        try
        {
            return SetOnWindow(hwnd, appUserModelId, $"\"{exePath}\"", displayName, $"{exePath},0");
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SetOnWindow(IntPtr hwnd, string appId, string relaunchCommand, string displayName, string iconResource)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null)
            return false;

        try
        {
            // The relaunch command and display name must be set together with the ID.
            SetString(store, PidId, appId);
            SetString(store, PidRelaunchCommand, relaunchCommand);
            SetString(store, PidRelaunchDisplayNameResource, displayName);
            SetString(store, PidRelaunchIconResource, iconResource);
            store.Commit();
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetString(IPropertyStore store, uint pid, string value)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormatId, PropertyId = pid };
        var variant = new PropVariant { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant.Pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

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

    /// <summary>PROPVARIANT holding a string pointer; padded to the full native size.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
