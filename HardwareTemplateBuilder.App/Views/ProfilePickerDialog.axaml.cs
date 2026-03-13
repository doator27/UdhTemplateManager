using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Modal dialog shown at every startup. The user must either select an existing
/// <see cref="UserProfile"/> or create a new one before the main window becomes usable.
/// The dialog cannot be closed or dismissed without making a selection.
/// </summary>
public partial class ProfilePickerDialog : Window
{
    private List<UserProfile> _profiles = new();

    /// <summary>Initializes the dialog.</summary>
    public ProfilePickerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var context = DatabaseInitializer.CreateContext();
        _profiles = new UserProfileRepository(context)
            .GetAll().OrderBy(u => u.UserName).ToList();

        ProfileList.ItemsSource = _profiles;
        ProfileList.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");

        if (_profiles.Count > 0)
            ProfileList.SelectedIndex = 0;

        SelectButton.Click  += (_, _) => SelectProfile();
        CreateButton.Click  += (_, _) => CreateAndSelect();
        BrowseButton.Click  += async (_, _) => await BrowseFolderAsync();
    }

    /// <summary>
    /// Prevents the dialog from being closed until a profile has been selected or created.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (SessionService.ActiveUserProfile == null)
            e.Cancel = true;

        base.OnClosing(e);
    }

    // ---------- Actions ----------

    private void SelectProfile()
    {
        if (ProfileList.SelectedItem is UserProfile p)
        {
            SessionService.ActiveUserProfile = p;
            Close();
        }
        else
        {
            SelectStatus.Text = "Please select a profile from the list.";
        }
    }

    private void CreateAndSelect()
    {
        var name     = NewNameBox.Text?.Trim();
        var location = NewLocationBox.Text?.Trim();

        if (string.IsNullOrEmpty(name))     { CreateStatus.Text = "Name is required."; return; }
        if (string.IsNullOrEmpty(location)) { CreateStatus.Text = "Save location is required."; return; }

        using var context = DatabaseInitializer.CreateContext();
        var profile = new UserProfileRepository(context).Add(new UserProfile
        {
            UserName = name,
            DefaultTemplateSaveLocation = location
        });

        SessionService.ActiveUserProfile = profile;
        Close();
    }

    private async Task BrowseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title       = "Select Job PDF Save Folder",
                AllowMultiple = false
            });

        if (folders.Count > 0)
            NewLocationBox.Text = folders[0].Path.LocalPath;
    }
}
