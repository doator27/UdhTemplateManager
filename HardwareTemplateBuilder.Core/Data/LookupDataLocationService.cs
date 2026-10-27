using System.IO;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Resolves the location of <c>data.json</c> — the exported snapshot of hardware item/template
/// metadata that the standalone Lookup app (<c>HardwareTemplateBuilder.Lookup</c>) reads instead
/// of connecting to the shared database directly.
/// <para>
/// Both the exporting app and the Lookup app must agree on the same fixed, shared location (a
/// per-exe-directory location doesn't work since the two apps run from different folders), so
/// this points at a fixed shared network folder rather than deriving a path from
/// <c>AppContext.BaseDirectory</c>.
/// </para>
/// <para>
/// Mapped drive letters are per-user-session: a different Windows user running the Lookup app
/// may have the same network share mapped to a different letter (or not mapped at all), even
/// though the file genuinely exists on the share. To avoid that mismatch, the configured drive
/// letter is resolved to its underlying UNC path (e.g. <c>\\server\share\...</c>) at runtime, so
/// the location is independent of any particular user's drive mappings.
/// </para>
/// </summary>
public static class LookupDataLocationService
{
    private const string FolderPath = @"T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TemplateLookupApp\data";
    private const string FileName = "data.json";

    private static string? _resolvedFolderPath;

    /// <summary>
    /// Returns the full path to <c>data.json</c>, creating the containing folder if it doesn't
    /// already exist.
    /// </summary>
    public static string GetDataFilePath()
    {
        var folder = ResolveFolderPath();
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileName);
    }

    /// <summary>
    /// Resolves <see cref="FolderPath"/> to a UNC path if its drive letter is a mapped network
    /// drive, falling back to the original drive-letter path if it isn't a network drive or the
    /// UNC path can't be resolved (e.g. the drive letter doesn't exist at all on this machine).
    /// </summary>
    private static string ResolveFolderPath()
    {
        _resolvedFolderPath ??= NetworkPathResolver.ResolveToUnc(FolderPath);
        return _resolvedFolderPath;
    }
}
