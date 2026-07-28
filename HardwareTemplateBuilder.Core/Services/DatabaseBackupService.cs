using System;
using System.IO;
using HardwareTemplateBuilder.Core.Data;
using Microsoft.Data.Sqlite;

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
/// Result of a database restore operation.
/// </summary>
public class RestoreResult
{
    /// <summary>Gets whether the restore completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the error message if the restore failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the path to the safety backup of the pre-restore live database, when one was made.
    /// Populated on both success and most failure paths (a failure after this step still leaves
    /// the pre-restore state recoverable from here).
    /// </summary>
    public string? SafetyBackupPath { get; init; }
}

/// <summary>
/// Service for creating and restoring backup copies of the SQLite database file.
/// </summary>
public class DatabaseBackupService
{
    private const string SafetyBackupPrefix = "hardware_templates_prerestore";

    /// <summary>The 16-byte header every valid SQLite database file begins with.</summary>
    private static readonly byte[] SqliteHeader =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// Creates a timestamped backup copy of the database at the specified location.
    /// The backup file is named: {filePrefix}_yyyy-MM-dd_HH-mm-ss.db
    /// </summary>
    /// <param name="sourceDatabasePath">Full path to the source database file.</param>
    /// <param name="backupDirectory">Directory where the backup file will be created.</param>
    /// <param name="filePrefix">
    /// Filename prefix. Defaults to the normal user-facing backup prefix; <see cref="RestoreBackup"/>
    /// passes a distinct prefix for the automatic pre-restore safety copy so it's identifiable.
    /// </param>
    /// <returns>A <see cref="BackupResult"/> indicating success or failure.</returns>
    public BackupResult CreateBackup(
        string sourceDatabasePath, string backupDirectory, string filePrefix = "hardware_templates_backup")
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
            var backupFileName = $"{filePrefix}_{timestamp}.db";
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
    /// Restores <paramref name="liveDatabasePath"/> from a previously-created backup file.
    /// This is a destructive operation on the live database, so every step is ordered to avoid
    /// touching it until the backup file has been validated and a safety copy of the current
    /// live database has been made. No step attempts an automatic revert if a later step fails —
    /// the safety backup path is always returned so the caller can recover manually.
    /// </summary>
    /// <param name="backupFilePath">Full path to the backup file to restore from.</param>
    /// <param name="liveDatabasePath">Full path to the live database file to overwrite.</param>
    /// <param name="safetyBackupDirectory">Directory to save the pre-restore safety backup into.</param>
    public RestoreResult RestoreBackup(string backupFilePath, string liveDatabasePath, string safetyBackupDirectory)
    {
        if (!File.Exists(backupFilePath))
        {
            return Fail($"Backup file not found: {backupFilePath}");
        }

        // Validate the file actually looks like a SQLite database before it ever touches the
        // live DB — a corrupt or wrong file must be rejected here, not after overwriting.
        try
        {
            using var stream = File.OpenRead(backupFilePath);
            if (stream.Length < SqliteHeader.Length)
                return Fail("The selected backup file is too small to be a valid database.");

            var header = new byte[SqliteHeader.Length];
            var read = stream.Read(header, 0, header.Length);
            if (read != header.Length || !HeaderMatches(header))
                return Fail("The selected backup file does not look like a valid SQLite database.");
        }
        catch (IOException ex)
        {
            return Fail($"Could not read the backup file: {ex.Message}");
        }

        // Safety copy of the current live database, so a bad restore is always recoverable.
        // Abort entirely if this fails — never overwrite the live DB without a safety net.
        string? safetyBackupPath = null;
        if (File.Exists(liveDatabasePath))
        {
            var safetyResult = CreateBackup(liveDatabasePath, safetyBackupDirectory, SafetyBackupPrefix);
            if (!safetyResult.Success)
                return Fail($"Restore aborted: could not create a safety backup first. {safetyResult.ErrorMessage}");

            safetyBackupPath = safetyResult.BackupPath;
        }

        // Release pooled native SQLite handles so the upcoming overwrite doesn't hit a file lock
        // left behind by a disposed-but-pooled connection.
        SqliteConnection.ClearAllPools();

        try
        {
            File.Copy(backupFilePath, liveDatabasePath, overwrite: true);
        }
        catch (IOException ex)
        {
            return new RestoreResult
            {
                Success = false,
                SafetyBackupPath = safetyBackupPath,
                ErrorMessage = $"Restore failed while copying the backup file — the live database was " +
                                $"NOT modified. {ex.Message}"
            };
        }

        // The live database has now been overwritten. Bring it up to the current schema in case
        // the backup predates a later migration/patch — this is the worst-case failure branch,
        // since a failure here means the live DB is already replaced.
        try
        {
            DatabaseInitializer.InitializeAtPath(liveDatabasePath);
        }
        catch (Exception ex)
        {
            var recovery = safetyBackupPath != null
                ? $" The live database has already been replaced with the selected backup. " +
                  $"Your previous database was saved to: {safetyBackupPath} — copy it back over " +
                  $"{liveDatabasePath} to recover."
                : string.Empty;
            return new RestoreResult
            {
                Success = false,
                SafetyBackupPath = safetyBackupPath,
                ErrorMessage = $"Restore completed but the restored database could not be prepared: " +
                                $"{ex.Message}.{recovery}"
            };
        }

        return new RestoreResult { Success = true, SafetyBackupPath = safetyBackupPath };
    }

    private static bool HeaderMatches(byte[] header)
    {
        for (int i = 0; i < SqliteHeader.Length; i++)
        {
            if (header[i] != SqliteHeader[i]) return false;
        }
        return true;
    }

    private static RestoreResult Fail(string message) => new() { Success = false, ErrorMessage = message };

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
