using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;

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
        SyncNowButton.Click += async (_, _) => await OnSyncNowAsync();
        RebuildMasterButton.Click += async (_, _) => await OnRebuildMasterAsync();
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

    private void SetRunningState(bool running)
    {
        SyncNowButton.IsEnabled = !running && !string.IsNullOrWhiteSpace(DatabaseInitializer.GetMasterPath());
        RebuildMasterButton.IsEnabled = !running;
        SyncProgress.IsVisible  = running;
    }
}
