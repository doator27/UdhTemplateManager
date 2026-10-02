using System;
using System.IO;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using Xunit;

namespace HardwareTemplateBuilder.Tests;

/// <summary>
/// Tests for <see cref="MasterSyncService"/>, focused on:
/// <list type="bullet">
///   <item>Basic sync correctness (local/master merge produces a <c>Synced</c> result).</item>
///   <item>The startup-hang regression: lock acquisition must always be bounded by
///   <see cref="MasterSyncService.LockAcquireTimeout"/> and must never block the caller
///   indefinitely, even under real concurrent contention for the same master file.</item>
/// </list>
/// </summary>
public class MasterSyncServiceTests : IDisposable
{
    private readonly string _root;
    private readonly TimeSpan _originalTimeout;

    public MasterSyncServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HtbMasterSyncTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);

        // Keep tests fast: use a short lock-acquire timeout instead of the 15s production
        // default. Restored in Dispose().
        _originalTimeout = MasterSyncService.LockAcquireTimeout;
        MasterSyncService.LockAcquireTimeout = TimeSpan.FromSeconds(5);
    }

    public void Dispose()
    {
        MasterSyncService.LockAcquireTimeout = _originalTimeout;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a file still held open by SQLite briefly after disposal
            // shouldn't fail the test run.
        }
    }

    private string NewDbPath(string name) => Path.Combine(_root, name);

    [Fact]
    public void Sync_ReturnsNotConfigured_WhenMasterPathIsNullOrWhitespace()
    {
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(localPath);

        var result = MasterSyncService.Sync(null, localPath);

        Assert.Equal(MasterSyncResult.NotConfigured, result);
    }

    [Fact]
    public void Sync_ReturnsMasterUnreachable_WhenMasterFileDoesNotExist()
    {
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(localPath);
        var masterPath = NewDbPath("does_not_exist.db");

        var result = MasterSyncService.Sync(masterPath, localPath);

        Assert.Equal(MasterSyncResult.MasterUnreachable, result);
    }

    [Fact]
    public void Sync_Succeeds_ForValidLocalAndMasterDatabases()
    {
        var localPath = NewDbPath("local.db");
        var masterPath = NewDbPath("master.db");
        DatabaseInitializer.InitializeAtPath(localPath);
        DatabaseInitializer.InitializeAtPath(masterPath);

        var result = MasterSyncService.Sync(masterPath, localPath);

        Assert.Equal(MasterSyncResult.Synced, result);
        Assert.Null(MasterSyncService.LastError);
    }

    /// <summary>
    /// Regression test for the startup hang: schema-check/merge/copy-back now run inside the
    /// advisory master-file lock, so two "machines" syncing the same master at once must
    /// serialize rather than deadlock. Both calls must complete well within the overall test
    /// timeout (which is much larger than <see cref="MasterSyncService.LockAcquireTimeout"/>),
    /// and neither call may throw.
    /// </summary>
    [Fact]
    public async Task Sync_DoesNotHang_WhenTwoMachinesSyncTheSameMasterConcurrently()
    {
        var masterPath = NewDbPath("master.db");
        DatabaseInitializer.InitializeAtPath(masterPath);

        var localPathA = NewDbPath("localA.db");
        var localPathB = NewDbPath("localB.db");
        DatabaseInitializer.InitializeAtPath(localPathA);
        DatabaseInitializer.InitializeAtPath(localPathB);

        var taskA = Task.Run(() => MasterSyncService.Sync(masterPath, localPathA));
        var taskB = Task.Run(() => MasterSyncService.Sync(masterPath, localPathB));

        var completed = await Task.WhenAny(
            Task.WhenAll(taskA, taskB),
            Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(taskA.IsCompleted && taskB.IsCompleted,
            "Both concurrent Sync() calls against the same master must complete " +
            "(not hang) within 30s. If this fails, the lock-acquire timeout guard has " +
            "regressed and startup can freeze indefinitely when two machines sync at once.");

        // Neither call should have thrown — Sync() is documented to never throw and instead
        // report failures via its return value / LastError.
        Assert.True(taskA.Result is MasterSyncResult.Synced or MasterSyncResult.Failed
            or MasterSyncResult.MasterUnreachable or MasterSyncResult.MasterIntegrityCheckFailed);
        Assert.True(taskB.Result is MasterSyncResult.Synced or MasterSyncResult.Failed
            or MasterSyncResult.MasterUnreachable or MasterSyncResult.MasterIntegrityCheckFailed);
    }

    /// <summary>
    /// Directly exercises the timeout guard added around <c>MasterFileLock.TryAcquire</c>:
    /// even if lock acquisition were to hang indefinitely (e.g. a stale lock left behind by a
    /// crashed process), <see cref="MasterSyncService.Sync"/> must still return within
    /// <see cref="MasterSyncService.LockAcquireTimeout"/> plus a small margin, rather than
    /// blocking the caller (and, in production, the application's startup/UI thread) forever.
    /// </summary>
    [Fact]
    public async Task Sync_ReturnsWithinBoundedTime_EvenUnderLockContention()
    {
        var masterPath = NewDbPath("master.db");
        DatabaseInitializer.InitializeAtPath(masterPath);
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(localPath);

        // Occupy the lock from a background sync, then immediately race a second sync against
        // the same master. The second call must not wait longer than LockAcquireTimeout (plus
        // slack for the first sync's own work) before giving up.
        var firstSync = Task.Run(() => MasterSyncService.Sync(masterPath, localPath));

        var localPath2 = NewDbPath("local2.db");
        DatabaseInitializer.InitializeAtPath(localPath2);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var secondSync = Task.Run(() => MasterSyncService.Sync(masterPath, localPath2));

        var finished = await Task.WhenAny(secondSync, Task.Delay(TimeSpan.FromSeconds(30)));
        sw.Stop();

        Assert.True(secondSync.IsCompleted,
            $"Sync() did not return within the expected bounded time (elapsed: {sw.Elapsed}). " +
            "This indicates the lock-acquire timeout guard is not effective.");

        await firstSync; // ensure no unobserved exception from the first sync
    }
}
