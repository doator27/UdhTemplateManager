using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// View for creating timestamped backup copies of the SQLite database.
/// Corresponds to the Maintenance menu "Backup Database" option.
/// </summary>
public partial class BackupDatabaseView : UserControl
{
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

        // Display database size
        if (File.Exists(_sourceDatabasePath))
        {
            var fileInfo = new FileInfo(_sourceDatabasePath);
            DatabaseSizeLabel.Text = $"Size: {DatabaseBackupService.FormatFileSize(fileInfo.Length)}";
        }

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

        // Load recent backups
        LoadRecentBackups();

        // Wire events
        BrowseButton.Click += async (_, _) => await BrowseForBackupLocationAsync();
        BackupButton.Click += async (_, _) => await CreateBackupAsync();
        BackButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        OpenBackupLocationButton.Click += (_, _) => OpenBackupLocation();
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
        }
    }

    /// <summary>
    /// Creates a timestamped backup of the database.
    /// </summary>
    private async Task CreateBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(_defaultBackupLocation))
        {
            StatusLabel.Foreground = Brushes.DarkRed;
            StatusLabel.Text = "Please select a backup location first.";
            return;
        }

        // Show progress
        ProgressPanel.IsVisible = true;
        BackupButton.IsEnabled = false;
        StatusLabel.Text = string.Empty;

        try
        {
            var service = new DatabaseBackupService();
            var result = await Task.Run(() => 
                service.CreateBackup(_sourceDatabasePath, _defaultBackupLocation));

            if (result.Success)
            {
                StatusLabel.Foreground = Brushes.DarkGreen;
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
                StatusLabel.Foreground = Brushes.DarkRed;
                StatusLabel.Text = $"Backup failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = Brushes.DarkRed;
            StatusLabel.Text = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            ProgressPanel.IsVisible = false;
            BackupButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Loads the list of recent backup files from the backup directory.
    /// </summary>
    private void LoadRecentBackups()
    {
        if (!Directory.Exists(_defaultBackupLocation))
        {
            RecentBackupsList.ItemsSource = new[] { "(No backups found)" };
            return;
        }

        try
        {
            var backupFiles = Directory.GetFiles(_defaultBackupLocation, "hardware_templates_backup_*.db")
                .OrderByDescending(File.GetLastWriteTime)
                .Take(10)
                .Select(f =>
                {
                    var fileInfo = new FileInfo(f);
                    var timestamp = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                    var size = DatabaseBackupService.FormatFileSize(fileInfo.Length);
                    return $"{Path.GetFileName(f)} — {timestamp} — {size}";
                })
                .ToList();

            RecentBackupsList.ItemsSource = backupFiles.Count > 0
                ? backupFiles
                : new[] { "(No backups found)" };
        }
        catch
        {
            RecentBackupsList.ItemsSource = new[] { "(Error reading backup folder)" };
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
            StatusLabel.Foreground = Brushes.DarkRed;
            StatusLabel.Text = $"Could not open folder: {ex.Message}";
        }
    }
}
