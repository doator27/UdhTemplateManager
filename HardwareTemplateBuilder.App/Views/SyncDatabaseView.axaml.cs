using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Maintenance view that lets the user manually trigger a sync between the local, per-machine
/// working database and the shared master database (see <see cref="MasterSyncService"/>)
/// without restarting the app. Mirrors the same merge-then-clean-copy flow that runs
/// automatically at startup.
/// </summary>
public partial class SyncDatabaseView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view and wires events on load.</summary>
    public SyncDatabaseView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var masterPath = DatabaseInitializer.GetMasterPath();
        MasterPathLabel.Text = string.IsNullOrWhiteSpace(masterPath)
            ? "No master database is configured. Set one via File \u2192 Database Location... to enable syncing."
            : $"Master database: {masterPath}";

        SyncNowButton.IsEnabled = !string.IsNullOrWhiteSpace(masterPath);
        ClearLockButton.IsEnabled = !string.IsNullOrWhiteSpace(masterPath);
        SyncNowButton.Click += async (_, _) => await OnSyncNowAsync();
        RebuildMasterButton.Click += async (_, _) => await OnRebuildMasterAsync();
        ClearLockButton.Click += async (_, _) => await OnClearLockAsync();
        CleanAndSyncButton.Click += async (_, _) => await OnCleanAndSyncAsync();
    }

    private async Task OnSyncNowAsync()
    {
        SetRunningState(running: true);
        RebuildMasterButton.IsVisible = false;
        StatusLabel.Foreground = Avalonia.Media.Brushes.Gray;
        StatusLabel.Text = "Syncing with master database...";

        var masterPath = DatabaseInitializer.GetMasterPath();
        var localPath  = DatabaseInitializer.GetDatabasePath();

        MasterSyncResult result;
        try
        {
            result = await Task.Run(() => MasterSyncService.Sync(masterPath, localPath));
        }
        catch (Exception ex)
        {
            SetRunningState(running: false);
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = $"Unexpected error: {ex.Message}";
            return;
        }

        SetRunningState(running: false);

        PopulateFailedRecords();

        switch (result)
        {
            case MasterSyncResult.Synced:
                StatusLabel.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
                StatusLabel.Text =
                    "Sync complete. New/changed local data has been merged into the master " +
                    "database, and this machine's local copy has been refreshed to match.";
                break;
            case MasterSyncResult.NotConfigured:
                StatusLabel.Foreground = Avalonia.Media.Brushes.Gray;
                StatusLabel.Text = "No master database is configured. Nothing to sync.";
                break;
            case MasterSyncResult.MasterUnreachable:
                StatusLabel.Foreground = Avalonia.Media.Brushes.Goldenrod;
                StatusLabel.Text =
                    "The master database could not be reached (offline, or another machine is " +
                    "currently syncing). Your local database is unchanged \u2014 try again shortly.";
                break;
            case MasterSyncResult.MasterIntegrityCheckFailed:
                StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
                StatusLabel.Text =
                    "The master database failed an integrity check. To avoid overwriting anyone " +
                    "else's data, sync was skipped and nothing was changed. Restore the master " +
                    "from a backup, or use \"Rebuild Master From This Machine\" below if you're " +
                    "sure this machine's local copy is the one to keep.";
                RebuildMasterButton.IsVisible = true;
                break;
            case MasterSyncResult.Failed:
            default:
                StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
                StatusLabel.Text = string.IsNullOrWhiteSpace(MasterSyncService.LastError)
                    ? "Sync failed. Your local database is unchanged \u2014 try again shortly."
                    : "Sync failed. Your local database is unchanged \u2014 try again shortly.\n" +
                      $"Details: {MasterSyncService.LastError}";
                break;
        }
    }

    private async Task OnRebuildMasterAsync()
    {
        var window = (Window?)VisualRoot;
        if (window == null)
            return;

        var confirmed = await DialogHelper.ConfirmAsync(
            window,
            "This will permanently overwrite the shared master database with this machine's " +
            "local copy. Any data added by other users since the master became unusable will " +
            "be lost. Continue?",
            "Rebuild Master Database");
        if (!confirmed)
            return;

        SetRunningState(running: true);
        StatusLabel.Foreground = Avalonia.Media.Brushes.Gray;
        StatusLabel.Text = "Rebuilding master database from this machine's local copy...";

        var masterPath = DatabaseInitializer.GetMasterPath();
        var localPath  = DatabaseInitializer.GetDatabasePath();

        await Task.Run(() => MasterSyncService.RebuildMasterFromLocal(masterPath, localPath));

        SetRunningState(running: false);
        RebuildMasterButton.IsVisible = false;

        if (string.IsNullOrWhiteSpace(MasterSyncService.LastError))
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
            StatusLabel.Text =
                "Master database rebuilt from this machine's local copy. Have other users run " +
                "Sync Now to re-share their local data.";
        }
        else
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = $"Rebuild failed. Details: {MasterSyncService.LastError}";
            RebuildMasterButton.IsVisible = true;
        }
    }

    private async Task OnClearLockAsync()
    {
        var window = (Window?)VisualRoot;
        if (window == null)
            return;

        var confirmed = await DialogHelper.ConfirmAsync(
            window,
            "This removes the sync lock file (and any leftover -wal/-shm/-journal files) next to " +
            "the master database. Only do this if you're sure no one else is actively syncing right " +
            "now — it never touches the master database file itself. Continue?",
            "Clear Stuck Lock");
        if (!confirmed)
            return;

        SetRunningState(running: true);
        StatusLabel.Foreground = Avalonia.Media.Brushes.Gray;
        StatusLabel.Text = "Clearing lock...";

        var masterPath = DatabaseInitializer.GetMasterPath();
        var message = await Task.Run(() => MasterSyncService.ForceClearLock(masterPath));

        SetRunningState(running: false);
        StatusLabel.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
        StatusLabel.Text = message;
    }

    private void SetRunningState(bool running)
    {
        SyncNowButton.IsEnabled = !running && !string.IsNullOrWhiteSpace(DatabaseInitializer.GetMasterPath());
        RebuildMasterButton.IsEnabled = !running;
        ClearLockButton.IsEnabled = !running && !string.IsNullOrWhiteSpace(DatabaseInitializer.GetMasterPath());
        SyncProgress.IsVisible  = running;
    }

    /// <summary>
    /// Rebuilds the "records that failed to sync" list from <see cref="MasterSyncService.LastFailedRecords"/>,
    /// each with a Delete button that removes that record (and everything referencing it) from
    /// the local and master databases via <see cref="RecordDeletionService"/>.
    /// </summary>
    private void PopulateFailedRecords()
    {
        FailedRecordsList.Children.Clear();

        var failedRecords = MasterSyncService.LastFailedRecords;
        FailedRecordsPanel.IsVisible = failedRecords.Count > 0;

        foreach (var record in failedRecords)
        {
            var pkDescription = string.Join(", ", record.PrimaryKey.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Avalonia.Thickness(0, 0, 0, 6)
            };

            var description = new TextBlock
            {
                Text = $"{record.TableName} ({pkDescription}): {record.Reason}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(description, 0);
            row.Children.Add(description);

            var deleteButton = new Button { Content = "Delete", Margin = new Avalonia.Thickness(0, 0, 4, 0) };
            Grid.SetColumn(deleteButton, 1);
            deleteButton.Click += async (_, _) => await OnDeleteFailedRecordAsync(record, row, force: false);
            row.Children.Add(deleteButton);

            var forceDeleteButton = new Button { Content = "Force Delete" };
            Avalonia.Controls.ToolTip.SetTip(forceDeleteButton, "Use if the record appears corrupted and normal Delete fails. Skips references it can't read or remove instead of aborting.");
            Grid.SetColumn(forceDeleteButton, 2);
            forceDeleteButton.Click += async (_, _) => await OnDeleteFailedRecordAsync(record, row, force: true);
            row.Children.Add(forceDeleteButton);

            FailedRecordsList.Children.Add(row);
        }
    }

    private async Task OnDeleteFailedRecordAsync(MasterSyncService.FailedRecord record, Control row, bool force)
    {
        var window = (Window?)VisualRoot;
        if (window == null)
            return;

        var pkDescription = string.Join(", ", record.PrimaryKey.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
        var confirmMessage = force
            ? $"This forcefully deletes the '{record.TableName}' record ({pkDescription}) from both the local " +
              "and master databases, even if it or its references appear corrupted. Any reference that can't " +
              "be read or removed will be skipped instead of stopping the whole operation. This cannot be " +
              "undone. Continue?"
            : $"This permanently deletes the '{record.TableName}' record ({pkDescription}) and every other " +
              "row that references it, from both the local and master databases. This cannot be undone. Continue?";
        var confirmed = await DialogHelper.ConfirmAsync(
            window,
            confirmMessage,
            force ? "Force Delete Record" : "Delete Record");
        if (!confirmed)
            return;

        SetRunningState(running: true);
        StatusLabel.Foreground = Avalonia.Media.Brushes.Gray;
        StatusLabel.Text = $"{(force ? "Force deleting" : "Deleting")} '{record.TableName}' record ({pkDescription})...";

        var masterPath = DatabaseInitializer.GetMasterPath();
        var localPath = DatabaseInitializer.GetDatabasePath();

        var result = await Task.Run(() =>
            RecordDeletionService.DeleteRecordEverywhere(record.TableName, record.PrimaryKey, masterPath, localPath, force));

        SetRunningState(running: false);
        StatusLabel.Foreground = result.Success ? Avalonia.Media.Brushes.MediumSeaGreen : Avalonia.Media.Brushes.IndianRed;
        StatusLabel.Text = result.Message;

        if (result.Success)
        {
            FailedRecordsList.Children.Remove(row);
            FailedRecordsPanel.IsVisible = FailedRecordsList.Children.Count > 0;
        }
    }

    private async Task OnCleanAndSyncAsync()
    {
        var window = (Window?)VisualRoot;
        if (window == null)
            return;

        var failedRecords = MasterSyncService.LastFailedRecords;
        if (failedRecords.Count == 0)
            return;

        var recordSummary = string.Join(
            "\n",
            failedRecords.Select(r =>
                $"  - {r.TableName} ({string.Join(", ", r.PrimaryKey.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"))})"));

        var confirmed = await DialogHelper.ConfirmAsync(
            window,
            "This backs up the master database, then permanently removes the following record(s) " +
            "(and any of their child records not used by anything else) from a clean copy of master, " +
            "merges this machine's local changes into that clean copy, and promotes it as the new master " +
            "and new local database. Every other machine will also skip these records going forward. " +
            "This cannot be undone:\n\n" + recordSummary,
            "Clean & Sync");
        if (!confirmed)
            return;

        SetRunningState(running: true);
        CleanAndSyncButton.IsEnabled = false;
        StatusLabel.Foreground = Avalonia.Media.Brushes.Gray;
        StatusLabel.Text = "Backing up master database and building a clean copy...";

        var masterPath = DatabaseInitializer.GetMasterPath();
        var localPath = DatabaseInitializer.GetDatabasePath();
        var backupDirectory = GetDefaultBackupLocation();

        var recordsToSkip = failedRecords
            .Select(r => new CorruptedRecordSkipService.SkipCandidate(r.TableName, r.PrimaryKey, r.Reason))
            .ToList();

        var result = await Task.Run(() =>
            CorruptedRecordSkipService.CreateCleanMasterAndSync(masterPath!, localPath, backupDirectory, recordsToSkip));

        SetRunningState(running: false);
        CleanAndSyncButton.IsEnabled = true;
        StatusLabel.Foreground = result.Success ? Avalonia.Media.Brushes.MediumSeaGreen : Avalonia.Media.Brushes.IndianRed;
        StatusLabel.Text = result.Message;

        if (result.Success)
            PopulateFailedRecords();
    }

    /// <summary>
    /// Resolves the standard backup directory the same way <c>BackupDatabaseView</c> does: the
    /// configured "DatabaseBackupLocation" app setting, or Documents\HardwareTemplateBackups if
    /// none has been configured.
    /// </summary>
    private static string GetDefaultBackupLocation()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var settingsRepo = new AppSettingRepository(ctx);
        var configured = settingsRepo.GetValue("DatabaseBackupLocation");

        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HardwareTemplateBackups");
    }

}
