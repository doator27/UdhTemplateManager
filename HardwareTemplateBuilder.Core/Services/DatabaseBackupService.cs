using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Result of a database backup operation.
/// </summary>
public class BackupResult
{
    /// <summary>Gets whether the backup completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the full path to the backup file if successful.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Gets the error message if the backup failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the size of the backed-up database file in bytes.</summary>
    public long FileSizeBytes { get; init; }
}

/// <summary>
/// Service for creating backup copies of the SQLite database file.
/// </summary>
public class DatabaseBackupService
{
    /// <summary>
    /// Creates a timestamped backup copy of the database at the specified location.
    /// The backup file is named: hardware_templates_backup_yyyy-MM-dd_HH-mm-ss.db
    /// </summary>
    /// <param name="sourceDatabasePath">Full path to the source database file.</param>
    /// <param name="backupDirectory">Directory where the backup file will be created.</param>
    /// <returns>A <see cref="BackupResult"/> indicating success or failure.</returns>
    public BackupResult CreateBackup(string sourceDatabasePath, string backupDirectory)
    {
        try
        {
            if (!File.Exists(sourceDatabasePath))
            {
                return new BackupResult
                {
                    Success = false,
                    ErrorMessage = $"Source database not found: {sourceDatabasePath}"
                };
            }

            // Create the backup directory if it doesn't exist
            Directory.CreateDirectory(backupDirectory);

            // Generate timestamped filename
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupFileName = $"hardware_templates_backup_{timestamp}.db";
            var backupPath = Path.Combine(backupDirectory, backupFileName);

            // Copy the database file
            File.Copy(sourceDatabasePath, backupPath, overwrite: false);

            // Get the file size for verification
            var fileInfo = new FileInfo(backupPath);

            // Verify the backup file exists and has content
            if (!File.Exists(backupPath) || fileInfo.Length == 0)
            {
                return new BackupResult
                {
                    Success = false,
                    ErrorMessage = "Backup file was created but appears to be empty or invalid."
                };
            }

            return new BackupResult
            {
                Success = true,
                BackupPath = backupPath,
                FileSizeBytes = fileInfo.Length
            };
        }
        catch (Exception ex)
        {
            return new BackupResult
            {
                Success = false,
                ErrorMessage = $"Backup failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Formats a file size in bytes to a human-readable string (KB, MB, etc.).
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>A formatted string like "1.5 MB".</returns>
    public static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
