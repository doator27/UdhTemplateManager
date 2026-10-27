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
/// </summary>
public static class LookupDataLocationService
{
    private const string FolderPath = @"T:\Server\LISTING DEPARTMENT\DTyler\Template Jobs\TemplateLookupApp\data";
    private const string FileName = "data.json";

    /// <summary>
    /// Returns the full path to <c>data.json</c>, creating the containing folder if it doesn't
    /// already exist.
    /// </summary>
    public static string GetDataFilePath()
    {
        Directory.CreateDirectory(FolderPath);
        return Path.Combine(FolderPath, FileName);
    }
}
