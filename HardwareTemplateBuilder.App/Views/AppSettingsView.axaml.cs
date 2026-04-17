using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Admin view for managing global application settings, such as the shared
/// template storage location and the active database file location.
/// </summary>
public partial class AppSettingsView : UserControl
{
    private AppSettingRepository? _repo;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public AppSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new AppSettingRepository(context);

        StorageLocationBox.Text  = _repo.GetValue("TemplateStorageLocation");
        CurrentDbPathLabel.Text  = DatabaseInitializer.GetDatabasePath();
        PointerFilePathLabel.Text = DatabaseLocationService.GetActivePointerFilePath();

        // Load SMTP settings
        SmtpHostBox.Text       = _repo.GetValue("SmtpHost");
        SmtpPortBox.Text       = _repo.GetValue("SmtpPort");
        SmtpUsernameBox.Text   = _repo.GetValue("SmtpUsername");
        SmtpPasswordBox.Text   = _repo.GetValue("SmtpPassword");
        AlertEmailToBox.Text   = _repo.GetValue("AlertEmailTo");
        AlertEmailFromBox.Text = _repo.GetValue("AlertEmailFrom");

        SaveButton.Click            += (_, _) => Save();
        BrowseButton.Click          += async (_, _) => await BrowseStorageFolderAsync();
        DbConnectBrowseButton.Click += async (_, _) => await BrowseConnectFileAsync();
        DbConnectButton.Click       += (_, _) => ConnectDatabase();
        DbMoveBrowseButton.Click    += async (_, _) => await BrowseDbTargetFolderAsync();
        MoveDbButton.Click          += (_, _) => MoveDatabase();
        SaveSmtpButton.Click        += (_, _) => SaveSmtp();
    }

    private async Task BrowseConnectFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select Existing Database File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("SQLite Database")
                        { Patterns = new[] { "*.db" } }
                }
            });

        if (files.Count > 0)
            DbConnectPathBox.Text = files[0].Path.LocalPath;
    }

    private void ConnectDatabase()
    {
        DbStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
        DbStatusLabel.Text = string.Empty;

        var path = DbConnectPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path)) { DbStatusLabel.Text = "Choose a database file first."; return; }
        if (!System.IO.File.Exists(path)) { DbStatusLabel.Text = "File not found or not accessible."; return; }

        try
        {
            DatabaseInitializer.InitializeAtPath(path);
            DatabaseLocationService.SetConfiguredPath(path);
            CurrentDbPathLabel.Text   = path;
            PointerFilePathLabel.Text = DatabaseLocationService.GetActivePointerFilePath();
            DbConnectPathBox.Text     = string.Empty;
            DbStatusLabel.Foreground  = Avalonia.Media.Brushes.DarkGreen;
            DbStatusLabel.Text = $"Connected. Restart the app to use the new database.";
        }
        catch (System.Exception ex)
        {
            DbStatusLabel.Text = $"Error connecting: {ex.Message}";
        }
    }

    private async Task BrowseStorageFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Template Storage Folder", AllowMultiple = false });

        if (folders.Count > 0)
            StorageLocationBox.Text = folders[0].Path.LocalPath;
    }

    private async Task BrowseDbTargetFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Target Folder for Database", AllowMultiple = false });

        if (folders.Count > 0)
            DbMoveTargetBox.Text = folders[0].Path.LocalPath;
    }

    private void Save()
    {
        var path = StorageLocationBox.Text?.Trim() ?? string.Empty;
        _repo!.SetValue("TemplateStorageLocation", path);
        StatusLabel.Text = "Saved.";
    }

    private void SaveSmtp()
    {
        _repo!.SetValue("SmtpHost",       SmtpHostBox.Text?.Trim()       ?? string.Empty);
        _repo!.SetValue("SmtpPort",       SmtpPortBox.Text?.Trim()       ?? "587");
        _repo!.SetValue("SmtpUsername",   SmtpUsernameBox.Text?.Trim()   ?? string.Empty);
        _repo!.SetValue("SmtpPassword",   SmtpPasswordBox.Text?.Trim()   ?? string.Empty);
        _repo!.SetValue("AlertEmailTo",   AlertEmailToBox.Text?.Trim()   ?? string.Empty);
        _repo!.SetValue("AlertEmailFrom", AlertEmailFromBox.Text?.Trim() ?? string.Empty);
        SmtpStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        SmtpStatusLabel.Text = "SMTP settings saved.";
    }

    private void MoveDatabase()
    {
        DbStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
        DbStatusLabel.Text = string.Empty;

        var targetFolder = DbMoveTargetBox.Text?.Trim();
        if (string.IsNullOrEmpty(targetFolder))
        {
            DbStatusLabel.Text = "Choose a target folder first.";
            return;
        }

        if (!Directory.Exists(targetFolder))
        {
            DbStatusLabel.Text = "Folder does not exist or is not accessible.";
            return;
        }

        var sourcePath = DatabaseInitializer.GetDatabasePath();
        var destPath   = Path.Combine(targetFolder, "hardware_templates.db");

        if (sourcePath.Equals(destPath, System.StringComparison.OrdinalIgnoreCase))
        {
            DbStatusLabel.Text = "The database is already at that location.";
            return;
        }

        try
        {
            File.Copy(sourcePath, destPath, overwrite: true);
            DatabaseLocationService.SetConfiguredPath(destPath);
            CurrentDbPathLabel.Text = destPath;
            DbMoveTargetBox.Text    = string.Empty;
            DbStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
            DbStatusLabel.Text = $"Database moved to {destPath}";
        }
        catch (System.Exception ex)
        {
            DbStatusLabel.Text = $"Error moving database: {ex.Message}";
        }
    }
}
