using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="UserProfile"/> records.</summary>
public partial class UserProfilesView : UserControl
{
    private UserProfileRepository? _repo;
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public UserProfilesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new UserProfileRepository(context);
        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        BrowseButton.Click += async (_, _) => await BrowseFolder();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this user profile?"))
                DeleteSelected();
        };
    }

    private async System.Threading.Tasks.Task BrowseFolder()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Template Save Location", AllowMultiple = false });

        if (folders.Count > 0)
            SaveLocationBox.Text = folders[0].Path.LocalPath;
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(u => string.IsNullOrEmpty(filter) || u.UserName.ToLower().Contains(filter))
            .OrderBy(u => u.UserName)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is UserProfile u)
        {
            _selectedId = u.Id;
            UserNameBox.Text = u.UserName;
            SaveLocationBox.Text = u.DefaultTemplateSaveLocation;
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var name = UserNameBox.Text?.Trim();
        var location = SaveLocationBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "User Name is required."; return; }
        if (string.IsNullOrEmpty(location)) { StatusLabel.Text = "Save Location is required."; return; }

        if (_selectedId == 0)
            _repo!.Add(new UserProfile { UserName = name, DefaultTemplateSaveLocation = location });
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null)
            {
                existing.UserName = name;
                existing.DefaultTemplateSaveLocation = location;
                _repo.Update(existing);
            }
        }
        StatusLabel.Text = "Saved.";
        LoadList();
    }

    private void DeleteSelected()
    {
        if (_selectedId == 0) return;
        _repo!.Delete(_selectedId);
        ClearForm();
        LoadList();
    }

    private void ClearForm()
    {
        _selectedId = 0;
        UserNameBox.Text = "";
        SaveLocationBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
