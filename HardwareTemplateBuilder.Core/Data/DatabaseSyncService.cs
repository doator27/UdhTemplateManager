using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Supports a "local working copy" multi-user model: each machine works against a private
/// local copy of the SQLite database, synced with the shared master copy on app start
/// (pull) and app close (push).
/// <para>
/// The master location is whatever <see cref="DatabaseLocationService"/> resolves to
/// (typically a shared network path). <see cref="DatabaseInitializer"/> always opens the
/// database at <see cref="GetLocalWorkingCopyPath"/> — the local copy — so all reads/writes
/// during a session happen locally, avoiding network-drive lock contention.
/// </para>
/// </summary>
public static class DatabaseSyncService
{
    private const string LocalCacheFolderName = "LocalCache";
    private const string LocalFileName        = "hardware_templates_local.db";

    /// <summary>Backoff delays (ms) between retries when the master file is locked by another user.</summary>
    private static readonly int[] RetryDelaysMs = { 1000, 2000, 5000, 10000, 20000 };

    private static readonly object _gate = new();
    private static bool _pending;
    private static bool _running;

    /// <summary>
    /// Queues a push-then-pull sync (local changes → master, then a fresh copy of master back
    /// down to local so autoincrement IDs and any other users' concurrent changes stay
    /// consistent). Call this after any successful local write (see
    /// <see cref="AppDbContext.SaveChanges"/>).
    /// <para>
    /// Multiple calls while a sync is already in flight are coalesced into a single extra pass
    /// once the current one finishes, so callers never need to await this directly. If the
    /// master is temporarily locked by another user's sync, the push is retried with backoff
    /// before giving up for this pass (a later save will trigger another attempt).
    /// </para>
    /// </summary>
    public static void QueueSync()
    {
        if (GetMasterPath() == null)
            return; // No shared location configured — local copy is authoritative, nothing to sync.

        lock (_gate)
        {
            _pending = true;
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Waits until any in-flight/queued sync started via <see cref="QueueSync"/> has finished.
    /// Call this before shutting down so a final, complete push to master is guaranteed.
    /// </summary>
    public static async Task WaitForIdleAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_running) return;
            }
            await Task.Delay(100);
        }
    }

    private static async Task ProcessQueueAsync()
    {
        while (true)
        {
            lock (_gate) { _pending = false; }

            try
            {
                await PushToMasterWithRetryAsync();

                // Master now reflects this change (and possibly other users' concurrent
                // changes) — pull a fresh copy back down so local autoincrement IDs and any
                // rows added elsewhere stay consistent with the master's canonical state.
                SyncFromMaster();
            }
            catch
            {
                // Best-effort: this save already succeeded locally. Give up on pushing for now —
                // the next local write, or the final sync at shutdown, will retry.
            }

            lock (_gate)
            {
                if (!_pending)
                {
                    _running = false;
                    return;
                }
                // Another save happened while we were syncing — loop and sync again.
            }
        }
    }

    /// <summary>
    /// Attempts <see cref="SyncToMaster"/>, retrying with backoff while the master file is
    /// locked by another user's in-progress sync, before letting the final failure propagate.
    /// </summary>
    private static async Task PushToMasterWithRetryAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SyncToMaster();
                return;
            }
            catch (IOException) when (attempt < RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt]);
            }
        }
    }

    /// <summary>
    /// Returns the path of this machine's private local working copy of the database.
    /// </summary>
    public static string GetLocalWorkingCopyPath()
    {
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appDataFolder, "HardwareTemplateBuilder", LocalCacheFolderName);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, LocalFileName);
    }

    /// <summary>
    /// Returns the shared master database path, or <c>null</c> if no shared location has been
    /// configured (in which case the local working copy is authoritative and no sync occurs).
    /// </summary>
    public static string? GetMasterPath() => DatabaseLocationService.GetConfiguredPath();

    /// <summary>
    /// Pulls the latest master database down to the local working copy. Call once at startup,
    /// before any context is opened against the local copy.
    /// <para>
    /// No-op if no master location is configured, or if the master file does not yet exist
    /// (first run — the local copy will become the master via <see cref="SyncToMaster"/>
    /// or by being promoted directly if the user never configures a shared location).
    /// </para>
    /// </summary>
    public static void SyncFromMaster()
    {
        var masterPath = GetMasterPath();
        if (masterPath == null || !File.Exists(masterPath))
            return;

        var localPath = GetLocalWorkingCopyPath();

        ClearPools();
        CopyDatabaseFile(masterPath, localPath);
    }

    /// <summary>
    /// Pushes the local working copy up to the shared master location, overwriting it.
    /// Call once at shutdown, after all contexts have been disposed.
    /// <para>No-op if no master location is configured or the local copy does not exist.</para>
    /// </summary>
    public static void SyncToMaster()
    {
        var masterPath = GetMasterPath();
        var localPath  = GetLocalWorkingCopyPath();
        if (masterPath == null || !File.Exists(localPath))
            return;

        ClearPools();

        var masterFolder = Path.GetDirectoryName(masterPath);
        if (!string.IsNullOrEmpty(masterFolder))
            Directory.CreateDirectory(masterFolder);

        CopyDatabaseFile(localPath, masterPath);
    }

    /// <summary>
    /// Clears Microsoft.Data.Sqlite's connection pools so no file handles remain open on
    /// either the local or master database files before a copy is attempted.
    /// </summary>
    private static void ClearPools() => SqliteConnection.ClearAllPools();

    /// <summary>
    /// Copies the SQLite file (and any leftover -wal/-shm sidecar files) from
    /// <paramref name="sourcePath"/> to <paramref name="destinationPath"/>, retrying briefly
    /// to ride out transient sharing violations on network drives.
    /// </summary>
    private static void CopyDatabaseFile(string sourcePath, string destinationPath)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                CopySidecarIfExists(sourcePath, destinationPath, "-wal");
                CopySidecarIfExists(sourcePath, destinationPath, "-shm");
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(250);
            }
        }
    }

    private static void CopySidecarIfExists(string sourcePath, string destinationPath, string suffix)
    {
        var sourceSidecar = sourcePath + suffix;
        if (!File.Exists(sourceSidecar))
            return;
        File.Copy(sourceSidecar, destinationPath + suffix, overwrite: true);
    }
}
