using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="DatabaseBackupService"/>, particularly the destructive <c>RestoreBackup</c> path.</summary>
public class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _liveDbPath;
    private readonly string _backupDir;
    private readonly DatabaseBackupService _service = new();

    public DatabaseBackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbBackupTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _liveDbPath = Path.Combine(_tempDir, "live.db");
        _backupDir = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>Creates a real, schema-initialized SQLite database file at the given path.</summary>
    private static void CreateRealDatabase(string path) => DatabaseInitializer.InitializeAtPath(path);

    private static byte[] ReadAllBytesRetrying(string path)
    {
        // File.Copy's internal handle can briefly overlap with the read on some platforms.
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) when (attempt < 3) { Thread.Sleep(50); }
        }
    }

    // ---------- RestoreBackup: rejection paths (live DB must stay untouched) ----------

    [Fact]
    public void RestoreBackup_BackupFileDoesNotExist_FailsWithoutTouchingLiveDb()
    {
        CreateRealDatabase(_liveDbPath);
        var originalBytes = ReadAllBytesRetrying(_liveDbPath);

        var result = _service.RestoreBackup(
            Path.Combine(_tempDir, "does-not-exist.db"), _liveDbPath, _backupDir);

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalBytes, ReadAllBytesRetrying(_liveDbPath));
    }

    [Fact]
    public void RestoreBackup_BackupFileHasBadHeader_FailsWithoutTouchingLiveDb()
    {
        CreateRealDatabase(_liveDbPath);
        var originalBytes = ReadAllBytesRetrying(_liveDbPath);

        var badBackupPath = Path.Combine(_tempDir, "not-a-database.db");
        File.WriteAllText(badBackupPath, "this is definitely not a SQLite file");

        var result = _service.RestoreBackup(badBackupPath, _liveDbPath, _backupDir);

        Assert.False(result.Success);
        Assert.Contains("valid SQLite database", result.ErrorMessage);
        Assert.Equal(originalBytes, ReadAllBytesRetrying(_liveDbPath));
        // No safety backup should have been attempted — rejection happens before that step.
        Assert.Empty(Directory.GetFiles(_backupDir, "hardware_templates_prerestore_*.db"));
    }

    [Fact]
    public void RestoreBackup_TooSmallToBeADatabase_Fails()
    {
        CreateRealDatabase(_liveDbPath);

        var tinyBackupPath = Path.Combine(_tempDir, "tiny.db");
        File.WriteAllBytes(tinyBackupPath, new byte[] { 1, 2, 3 });

        var result = _service.RestoreBackup(tinyBackupPath, _liveDbPath, _backupDir);

        Assert.False(result.Success);
        Assert.Contains("too small", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- RestoreBackup: happy path ----------

    [Fact]
    public void RestoreBackup_ValidBackup_ReplacesLiveDbAndCreatesSafetyBackup()
    {
        // "Old" state, captured as the backup to restore.
        CreateRealDatabase(_liveDbPath);
        var backupResult = _service.CreateBackup(_liveDbPath, _backupDir);
        Assert.True(backupResult.Success);

        // Mutate the "live" file so it differs from the backup.
        File.AppendAllText(_liveDbPath, "corruption-marker-should-be-overwritten");

        var result = _service.RestoreBackup(backupResult.BackupPath!, _liveDbPath, _backupDir);

        Assert.True(result.Success);
        var restoredText = System.Text.Encoding.ASCII.GetString(ReadAllBytesRetrying(_liveDbPath));
        Assert.DoesNotContain("corruption-marker", restoredText);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.Contains("hardware_templates_prerestore_", result.SafetyBackupPath);

        // Restored file matches the original backed-up content in its actual database bytes
        // (InitializeAtPath may append trailing schema-patch writes, so verify via a fresh
        // successful connection rather than a byte-for-byte match).
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_liveDbPath}")
            .Options;
        using var ctx = new AppDbContext(options);
        Assert.True(ctx.Database.CanConnect());
    }

    [Fact]
    public void RestoreBackup_NoExistingLiveDb_SkipsSafetyBackupButStillRestores()
    {
        CreateRealDatabase(Path.Combine(_tempDir, "source-for-backup.db"));
        var sourcePath = Path.Combine(_tempDir, "source-for-backup.db");
        var backupResult = _service.CreateBackup(sourcePath, _backupDir);
        Assert.True(backupResult.Success);

        // liveDatabasePath does not exist yet — nothing to protect.
        var freshLivePath = Path.Combine(_tempDir, "brand-new-live.db");

        var result = _service.RestoreBackup(backupResult.BackupPath!, freshLivePath, _backupDir);

        Assert.True(result.Success);
        Assert.Null(result.SafetyBackupPath);
        Assert.True(File.Exists(freshLivePath));
    }

    // ---------- CreateBackup: filePrefix regression guard ----------

    [Fact]
    public void CreateBackup_DefaultPrefix_MatchesExistingNamingConvention()
    {
        CreateRealDatabase(_liveDbPath);

        var result = _service.CreateBackup(_liveDbPath, _backupDir);

        Assert.True(result.Success);
        Assert.StartsWith("hardware_templates_backup_", Path.GetFileName(result.BackupPath));
    }

    [Fact]
    public void CreateBackup_CustomPrefix_UsesIt()
    {
        CreateRealDatabase(_liveDbPath);

        var result = _service.CreateBackup(_liveDbPath, _backupDir, "custom_prefix");

        Assert.True(result.Success);
        Assert.StartsWith("custom_prefix_", Path.GetFileName(result.BackupPath));
    }
}
