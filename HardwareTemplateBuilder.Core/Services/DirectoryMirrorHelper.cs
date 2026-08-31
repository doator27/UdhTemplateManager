using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Best-effort helpers for duplicating job output into a secondary save location. Failures are
/// swallowed (never thrown) since mirroring a copy must never block or fail the primary
/// operation that already succeeded.
/// </summary>
public static class DirectoryMirrorHelper
{
    /// <summary>
    /// Recursively copies every file and subfolder from <paramref name="sourceDir"/> into
    /// <paramref name="destDir"/>, overwriting existing files. Files/folders that exist only in
    /// <paramref name="destDir"/> are left untouched. Does nothing if <paramref name="sourceDir"/>
    /// does not exist. Any I/O error is swallowed; this is a best-effort copy.
    /// </summary>
    public static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
                return;

            Directory.CreateDirectory(destDir);

            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                var destPath = Path.Combine(destDir, Path.GetFileName(filePath));
                try { File.Copy(filePath, destPath, overwrite: true); } catch { /* best-effort */ }
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectoryContents(subDir, destSubDir);
            }
        }
        catch
        {
            // Mirroring is a best-effort secondary copy; never let it fail the caller.
        }
    }

    /// <summary>
    /// Copies a single file from <paramref name="sourceFilePath"/> into <paramref name="destDir"/>
    /// (created if needed), preserving the source file name and overwriting any existing file.
    /// Any I/O error is swallowed; this is a best-effort copy.
    /// </summary>
    public static void CopyFile(string sourceFilePath, string destDir)
    {
        try
        {
            if (!File.Exists(sourceFilePath))
                return;

            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(sourceFilePath));
            File.Copy(sourceFilePath, destPath, overwrite: true);
        }
        catch
        {
            // Mirroring is a best-effort secondary copy; never let it fail the caller.
        }
    }

    /// <summary>
    /// Deletes a same-named file from <paramref name="destDir"/> if it exists, mirroring a
    /// removal made in the primary location. Any I/O error is swallowed.
    /// </summary>
    public static void DeleteFile(string fileName, string destDir)
    {
        try
        {
            var destPath = Path.Combine(destDir, fileName);
            if (File.Exists(destPath))
                File.Delete(destPath);
        }
        catch
        {
            // Mirroring is a best-effort secondary copy; never let it fail the caller.
        }
    }
}
