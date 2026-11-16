using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Ui;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.Wpf.Views;

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
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        BrowseJobLocationButton.Click += async (_, _) => await BrowseJobLocationAsync();
        ClearJobLocationButton.Click += (_, _) => CustomJobSaveLocationBox.Text = "";
        DeleteButton.Click += async (_, _) =>
        {
            var window = this.OwnerWindow();
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this user profile?"))
                DeleteSelected();
        };
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(u => string.IsNullOrEmpty(filter) || u.UserName.ToLower().Contains(filter))
            .OrderBy(u => u.UserName)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberPath = "UserName";
    }

    private async Task BrowseJobLocationAsync()
    {
        await Task.CompletedTask;
        var folder = FilePickers.PickFolder(this.OwnerWindow(), "Select Custom Job Save Location");
        if (folder != null)
            CustomJobSaveLocationBox.Text = folder;
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is UserProfile u)
        {
            _selectedId = u.Id;
            UserNameBox.Text = u.UserName;
            CustomJobSaveLocationBox.Text = u.CustomJobSaveLocation ?? "";
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var name = UserNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "User Name is required."; return; }
        var customJobLocation = CustomJobSaveLocationBox.Text?.Trim();

        if (_selectedId == 0)
            _repo!.Add(new UserProfile
            {
                UserName = name,
                DefaultTemplateSaveLocation = string.Empty,
                CustomJobSaveLocation = string.IsNullOrEmpty(customJobLocation) ? null : customJobLocation
            });
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null)
            {
                existing.UserName = name;
                existing.CustomJobSaveLocation = string.IsNullOrEmpty(customJobLocation) ? null : customJobLocation;
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
        CustomJobSaveLocationBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
