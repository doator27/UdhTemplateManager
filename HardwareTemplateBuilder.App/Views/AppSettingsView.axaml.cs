using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Admin view for managing global application settings, such as the shared
/// template storage location and the active SQL Server database connection.
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
        CurrentDbPathLabel.Text  = DatabaseInitializer.GetConnectionString();
        PointerFilePathLabel.Text = DatabaseLocationService.GetActivePointerFilePath();

        SaveButton.Click      += (_, _) => Save();
        BrowseButton.Click    += async (_, _) => await BrowseStorageFolderAsync();
        DbConnectButton.Click += (_, _) => ConnectDatabase();
    }

    private void ConnectDatabase()
    {
        DbStatusLabel.Foreground = AppColors.Danger;
        DbStatusLabel.Text = string.Empty;

        var connectionString = DbConnectPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(connectionString))
        {
            DbStatusLabel.Text = "Enter a connection string first.";
            return;
        }

        try
        {
            DatabaseInitializer.InitializeAtConnectionString(connectionString);
            DatabaseLocationService.SetConfiguredConnectionString(connectionString);
            CurrentDbPathLabel.Text   = connectionString;
            PointerFilePathLabel.Text = DatabaseLocationService.GetActivePointerFilePath();
            DbConnectPathBox.Text     = string.Empty;
            DbStatusLabel.Foreground  = AppColors.Success;
            DbStatusLabel.Text = "Connected. Restart the app to use the new database.";
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

    private void Save()
    {
        var path = StorageLocationBox.Text?.Trim() ?? string.Empty;
        _repo!.SetValue("TemplateStorageLocation", path);
        StatusLabel.Text = "Saved.";
    }
}
