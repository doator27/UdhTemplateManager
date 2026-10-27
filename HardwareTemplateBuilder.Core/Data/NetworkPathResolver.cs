using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Resolves mapped-drive-letter paths (e.g. <c>T:\some\folder</c>) to their underlying UNC path
/// (e.g. <c>\\server\share\some\folder</c>).
/// <para>
/// Mapped drive letters are per-user-session: two different Windows users (or the same user on
/// two different machines) can have the same network share mapped to different letters, or not
/// mapped at all. Paths persisted to a shared location (database values, exported snapshots,
/// etc.) that embed a drive letter are therefore fragile across users/machines. Resolving to the
/// UNC form at the point of use/storage removes that dependency.
/// </para>
/// <para>
/// If the given path isn't rooted at a mapped network drive (e.g. it's already a UNC path, a
/// local disk, or the drive letter isn't mapped on this machine), the original path is returned
/// unchanged.
/// </para>
/// </summary>
public static class NetworkPathResolver
{
    private static readonly ConcurrentDictionary<string, string?> UncRootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="path"/> rewritten to use its drive letter's UNC root, or the
    /// original <paramref name="path"/> unchanged if it isn't a mapped network drive path or
    /// can't be resolved on this machine.
    /// </summary>
    public static string ResolveToUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':')
            return path;

        var driveLetter = path.Substring(0, 2);
        var uncRoot = GetUncRoot(driveLetter);
        if (uncRoot == null)
            return path;

        return uncRoot.TrimEnd('\\') + path.Substring(2);
    }

    private static string? GetUncRoot(string driveLetter)
    {
        return UncRootCache.GetOrAdd(driveLetter, TryGetUncRoot);
    }

    private static string? TryGetUncRoot(string driveLetter)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var sb = new StringBuilder(512);
        var length = sb.Capacity;
        var result = WNetGetConnection(driveLetter, sb, ref length);
        return result == 0 ? sb.ToString() : null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(
        [MarshalAs(UnmanagedType.LPWStr)] string localName,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder remoteName,
        ref int length);
}
