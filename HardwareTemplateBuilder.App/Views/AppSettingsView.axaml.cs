using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Admin view for managing global application settings, such as the shared
/// template storage location used by all template refresh operations.
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

        StorageLocationBox.Text = _repo.GetValue("TemplateStorageLocation");

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        SaveButton.Click += (_, _) => Save();
        BrowseButton.Click += async (_, _) => await BrowseFolderAsync();
    }

    private async System.Threading.Tasks.Task BrowseFolderAsync()
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
