using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Modal dialog shown at startup when no profile is bound to the current machine.
/// The user must select an existing <see cref="UserProfile"/> or create a new one.
/// Selecting a profile binds it to the current machine so this dialog is skipped
/// on all future launches on the same machine.
/// The dialog cannot be closed without making a selection.
/// </summary>
public partial class ProfilePickerDialog : Window
{
    private readonly string _machineId;
    private List<ProfileDisplay> _displayItems = new();

    /// <summary>Required by the Avalonia XAML compiler; delegates to the parameterized constructor.</summary>
    public ProfilePickerDialog() : this(string.Empty) { }

    /// <summary>Initializes the dialog for the given machine identity.</summary>
    /// <param name="machineId">The stable ID of the current machine, used to bind the chosen profile.</param>
    public ProfilePickerDialog(string machineId)
    {
        _machineId = machineId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var context = DatabaseInitializer.CreateContext();
        var profiles = new UserProfileRepository(context)
            .GetAll().OrderBy(u => u.UserName).ToList();

        _displayItems = profiles
            .Select(p => new ProfileDisplay(p, _machineId))
            .ToList();

        ProfileList.ItemsSource = _displayItems;
        ProfileList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        if (_displayItems.Count > 0)
            ProfileList.SelectedIndex = 0;

        SelectButton.Click += (_, _) => SelectProfile();
        CreateButton.Click += (_, _) => CreateAndSelect();
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
        if (ProfileList.SelectedItem is not ProfileDisplay d)
        {
            SelectStatus.Text = "Please select a profile from the list.";
            return;
        }

        // Bind this machine to the selected profile.
        using var context = DatabaseInitializer.CreateContext();
        var profile = context.UserProfiles.Find(d.Profile.Id);
        if (profile != null)
        {
            profile.MachineId = _machineId;
            context.SaveChanges();
        }

        SessionService.ActiveUserProfile = profile ?? d.Profile;
        Close();
    }

    private void CreateAndSelect()
    {
        var name = NewNameBox.Text?.Trim();

        if (string.IsNullOrEmpty(name)) { CreateStatus.Text = "Name is required."; return; }

        using var context = DatabaseInitializer.CreateContext();
        var profile = new UserProfileRepository(context).Add(new UserProfile
        {
            UserName                    = name,
            DefaultTemplateSaveLocation = string.Empty,
            MachineId                   = _machineId
        });

        SessionService.ActiveUserProfile = profile;
        Close();
    }

    // ---------- Inner display wrapper ----------

    /// <summary>Wraps a <see cref="UserProfile"/> with a display label for the picker list.</summary>
    private sealed class ProfileDisplay
    {
        /// <summary>Gets the underlying profile.</summary>
        public UserProfile Profile { get; }

        /// <summary>Gets the text shown in the list, annotated with "(this machine)" when applicable.</summary>
        public string DisplayText { get; }

        /// <summary>Initializes a new <see cref="ProfileDisplay"/>.</summary>
        public ProfileDisplay(UserProfile profile, string currentMachineId)
        {
            Profile     = profile;
            DisplayText = profile.MachineId == currentMachineId
                ? $"{profile.UserName} (this machine)"
                : profile.UserName;
        }
    }
}
