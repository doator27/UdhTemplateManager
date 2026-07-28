using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// View for creating timestamped backup copies of the SQLite database, and restoring the
/// database from a previously-created backup. Corresponds to the Maintenance menu
/// "Backup Database" option.
/// </summary>
public partial class BackupDatabaseView : UserControl
{
    /// <summary>Prefix used for the automatic safety backup taken right before a restore.</summary>
    private const string SafetyBackupPrefix = "hardware_templates_prerestore";

    /// <summary>Row wrapper for the recent-backups list, carrying the file's full path.</summary>
    private sealed class BackupFileRow
    {
        public string FullPath { get; }
        public string FileName { get; }
        public DateTime Timestamp { get; }
        public long SizeBytes { get; }
        public bool IsPreRestoreSafetyBackup { get; }

        public string Display =>
            $"{FileName} — {Timestamp:yyyy-MM-dd HH:mm:ss} — {DatabaseBackupService.FormatFileSize(SizeBytes)}"
            + (IsPreRestoreSafetyBackup ? "  [pre-restore safety copy]" : "");

        public BackupFileRow(string fullPath)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            var info = new FileInfo(fullPath);
            Timestamp = info.LastWriteTime;
            SizeBytes = info.Length;
            IsPreRestoreSafetyBackup = FileName.StartsWith(SafetyBackupPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string _sourceDatabasePath = string.Empty;
    private string _defaultBackupLocation = string.Empty;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the backup database view.</summary>
    public BackupDatabaseView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        // Get the current database path
        _sourceDatabasePath = DatabaseInitializer.GetDatabasePath();
        DatabasePathLabel.Text = _sourceDatabasePath;
        RefreshDatabaseSizeLabel();

        // Get the default backup location from app settings
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var settingsRepo = new AppSettingRepository(ctx);
            var configured = settingsRepo.GetValue("DatabaseBackupLocation");

            if (!string.IsNullOrWhiteSpace(configured))
            {
                _defaultBackupLocation = configured;
            }
            else
            {
                // Default to Documents/HardwareTemplateBackups
                _defaultBackupLocation = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "HardwareTemplateBackups");
            }
        }

        BackupPathBox.Text = _defaultBackupLocation;
        BackupLocationLabel.Text = $"({_defaultBackupLocation})";

        RecentBackupsList.ItemTemplate = new FuncDataTemplate<BackupFileRow>((row, _) => new TextBlock
        {
            Text = row?.Display ?? string.Empty,
            Foreground = (row?.IsPreRestoreSafetyBackup == true) ? AppColors.Warning : AppColors.Primary,
            Padding = new Avalonia.Thickness(2)
        }, supportsRecycling: false);

        RecentBackupsList.SelectionChanged += (_, _) =>
            RestoreButton.IsEnabled = RecentBackupsList.SelectedItem is BackupFileRow;

        // Load recent backups
        LoadRecentBackups();

        // Wire events
        BrowseButton.Click += async (_, _) => await BrowseForBackupLocationAsync();
        BackupButton.Click += async (_, _) => await CreateBackupAsync();
        RestoreButton.Click += async (_, _) => await RestoreSelectedBackupAsync();
        BackButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        OpenBackupLocationButton.Click += (_, _) => OpenBackupLocation();
    }

    private void RefreshDatabaseSizeLabel()
    {
        DatabaseSizeLabel.Text = File.Exists(_sourceDatabasePath)
            ? $"Size: {DatabaseBackupService.FormatFileSize(new FileInfo(_sourceDatabasePath).Length)}"
            : string.Empty;
    }

    /// <summary>
    /// Opens a folder picker to allow the user to select a backup destination.
    /// </summary>
    private async Task BrowseForBackupLocationAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Backup Location",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0]?.Path != null)
        {
            _defaultBackupLocation = folders[0].Path.LocalPath;
            BackupPathBox.Text = _defaultBackupLocation;
            BackupLocationLabel.Text = $"({_defaultBackupLocation})";

            // Save the new location to app settings
            using var ctx = DatabaseInitializer.CreateContext();
            var settingsRepo = new AppSettingRepository(ctx);
            settingsRepo.SetValue("DatabaseBackupLocation", _defaultBackupLocation);

            LoadRecentBackups();
        }
    }

    /// <summary>
    /// Creates a timestamped backup of the database.
    /// </summary>
    private async Task CreateBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(_defaultBackupLocation))
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = "Please select a backup location first.";
            return;
        }

        // Show progress
        SetProgressState(running: true, "Creating backup...");
        StatusLabel.Text = string.Empty;

        try
        {
            var service = new DatabaseBackupService();
            var result = await Task.Run(() =>
                service.CreateBackup(_sourceDatabasePath, _defaultBackupLocation));

            if (result.Success)
            {
                StatusLabel.Foreground = AppColors.Success;
                StatusLabel.Text = $"Backup created successfully: {Path.GetFileName(result.BackupPath!)}\n" +
                                 $"Size: {DatabaseBackupService.FormatFileSize(result.FileSizeBytes)}";

                // Reload recent backups list
                LoadRecentBackups();

                // Ask if the user wants to open the backup folder
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window != null)
                {
                    bool openFolder = await DialogHelper.ConfirmAsync(window,
                        "Backup created successfully!\n\nWould you like to open the backup folder?",
                        "Backup Complete");

                    if (openFolder)
                    {
                        OpenBackupLocation();
                    }
                }
            }
            else
            {
                StatusLabel.Foreground = AppColors.Danger;
                StatusLabel.Text = $"Backup failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            SetProgressState(running: false, "Creating backup...");
        }
    }

    /// <summary>
    /// Restores the live database from the backup selected in <see cref="RecentBackupsList"/>,
    /// after an explicit confirmation. Makes a safety backup of the current live database first
    /// (see <see cref="DatabaseBackupService.RestoreBackup"/>) and, on success, offers to restart
    /// the application so no stale in-memory state (session, loaded views) survives the swap.
    /// </summary>
    private async Task RestoreSelectedBackupAsync()
    {
        if (RecentBackupsList.SelectedItem is not BackupFileRow row) return;

        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        // Re-validate right before acting — the list may be stale if the file was deleted
        // externally since it was loaded.
        if (!File.Exists(row.FullPath))
        {
            await DialogHelper.ShowInfoAsync(window,
                $"\"{row.FileName}\" no longer exists at its expected location. The backup list will be refreshed.",
                "Backup No Longer Available");
            LoadRecentBackups();
            return;
        }

        bool confirmed = await DialogHelper.ConfirmAsync(window,
            $"Restore the database from:\n\n{row.FileName}\n({row.Timestamp:yyyy-MM-dd HH:mm:ss})\n\n" +
            "All changes made since that backup will be PERMANENTLY LOST.\n\n" +
            "A safety copy of the current database will be made automatically before restoring, " +
            "in case you need to undo this.\n\nContinue?",
            "Restore Database — This Cannot Be Undone");
        if (!confirmed) return;

        SetProgressState(running: true, "Restoring backup...");
        StatusLabel.Text = string.Empty;

        RestoreResult result;
        try
        {
            var backupPath = row.FullPath;
            result = await Task.Run(() =>
                new DatabaseBackupService().RestoreBackup(backupPath, _sourceDatabasePath, _defaultBackupLocation));
        }
        catch (Exception ex)
        {
            result = new RestoreResult { Success = false, ErrorMessage = $"Unexpected error: {ex.Message}" };
        }
        finally
        {
            SetProgressState(running: false, "Creating backup...");
        }

        if (result.Success)
        {
            StatusLabel.Foreground = AppColors.Success;
            StatusLabel.Text = "Database restored successfully."
                + (result.SafetyBackupPath != null
                    ? $"\nYour previous database was saved to: {result.SafetyBackupPath}"
                    : string.Empty);

            RefreshDatabaseSizeLabel();
            LoadRecentBackups();

            bool restartNow = await DialogHelper.ConfirmAsync(window,
                "The database has been restored." +
                (result.SafetyBackupPath != null
                    ? $"\n\nYour previous database was saved to:\n{result.SafetyBackupPath}"
                    : string.Empty) +
                "\n\nThe application must restart before continuing — data loaded into memory during " +
                "this session no longer matches what's on disk. Do not keep using the app without restarting.\n\n" +
                "Restart now?",
                "Restore Complete — Restart Required");

            if (restartNow)
            {
                if (!ProcessRelaunchHelper.RelaunchAndExit(window))
                {
                    await DialogHelper.ShowInfoAsync(window,
                        "Could not restart the application automatically. Please close and reopen it manually.",
                        "Manual Restart Needed");
                }
            }
        }
        else
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = "Restore failed — see details.";
            await DialogHelper.ShowScrollableInfoAsync(window, result.ErrorMessage ?? "Unknown error.", "Restore Failed");
        }
    }

    /// <summary>
    /// Loads the list of recent backup files (including pre-restore safety backups) from the
    /// backup directory.
    /// </summary>
    private void LoadRecentBackups()
    {
        RestoreButton.IsEnabled = false;

        if (!Directory.Exists(_defaultBackupLocation))
        {
            RecentBackupsList.ItemsSource = null;
            NoBackupsLabel.IsVisible = true;
            return;
        }

        try
        {
            var backupFiles = Directory.GetFiles(_defaultBackupLocation, "hardware_templates_backup_*.db")
                .Concat(Directory.GetFiles(_defaultBackupLocation, $"{SafetyBackupPrefix}_*.db"))
                .OrderByDescending(File.GetLastWriteTime)
                .Take(15)
                .Select(f => new BackupFileRow(f))
                .ToList();

            RecentBackupsList.ItemsSource = null;
            RecentBackupsList.ItemsSource = backupFiles;
            NoBackupsLabel.IsVisible = backupFiles.Count == 0;
        }
        catch (Exception ex)
        {
            RecentBackupsList.ItemsSource = null;
            NoBackupsLabel.IsVisible = true;
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Could not read the backup folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the backup folder in the OS file explorer.
    /// </summary>
    private void OpenBackupLocation()
    {
        if (!Directory.Exists(_defaultBackupLocation))
        {
            Directory.CreateDirectory(_defaultBackupLocation);
        }

        try
        {
            Process.Start(new ProcessStartInfo(_defaultBackupLocation) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private void SetProgressState(bool running, string progressText)
    {
        ProgressLabel.Text = progressText;
        ProgressPanel.IsVisible = running;
        BackupButton.IsEnabled = !running;
        RestoreButton.IsEnabled = !running && RecentBackupsList.SelectedItem is BackupFileRow;
    }
}
