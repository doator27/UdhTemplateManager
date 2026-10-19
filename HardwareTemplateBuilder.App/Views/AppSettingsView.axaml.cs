using System.Linq;
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

        BrowseSqliteButton.Click         += async (_, _) => await BrowseSqliteFileAsync();
        UploadMissingRecordsButton.Click += async (_, _) => await UploadMissingRecordsAsync();
    }

    private async Task BrowseSqliteFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Old SQLite Database",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Database") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count > 0)
            SqliteMergePathBox.Text = files[0].Path.LocalPath;
    }

    private async Task UploadMissingRecordsAsync()
    {
        SqliteMergeStatusLabel.Foreground = AppColors.Danger;
        SqliteMergeStatusLabel.Text = string.Empty;

        var sqlitePath = SqliteMergePathBox.Text?.Trim();
        if (string.IsNullOrEmpty(sqlitePath))
        {
            SqliteMergeStatusLabel.Text = "Select an old SQLite database file first.";
            return;
        }

        UploadMissingRecordsButton.IsEnabled = false;
        try
        {
            using var context = DatabaseInitializer.CreateContext();
            var results = await SqliteMergeService.MergeMissingRecordsAsync(sqlitePath, context);

            var totalInserted = results.Sum(r => r.InsertedCount);
            SqliteMergeStatusLabel.Foreground = AppColors.Success;
            SqliteMergeStatusLabel.Text = totalInserted == 0
                ? "No missing records found. The active database already has everything from that file."
                : $"Done. Inserted {totalInserted} missing record(s): " +
                  string.Join(", ", results.Where(r => r.InsertedCount > 0).Select(r => $"{r.TableName}={r.InsertedCount}"));
        }
        catch (System.Exception ex)
        {
            SqliteMergeStatusLabel.Foreground = AppColors.Danger;
            SqliteMergeStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            UploadMissingRecordsButton.IsEnabled = true;
        }
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
