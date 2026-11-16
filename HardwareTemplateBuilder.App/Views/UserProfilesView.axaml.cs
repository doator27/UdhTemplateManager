using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using System.Linq;
using System.Threading.Tasks;

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
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += async (_, _) => await SaveAsync();
        MigrateJobsButton.Click += async (_, _) => await MigrateJobsForSelectedAsync();
        NewButton.Click += (_, _) => ClearForm();
        BrowseJobLocationButton.Click += async (_, _) => await BrowseJobLocationAsync();
        ClearJobLocationButton.Click += (_, _) => CustomJobSaveLocationBox.Text = "";
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
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
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");
    }

    private async Task BrowseJobLocationAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Job Save Location", AllowMultiple = false });

        if (folders.Count > 0)
            CustomJobSaveLocationBox.Text = folders[0].Path.LocalPath;
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

    private async Task SaveAsync()
    {
        var name = UserNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "User Name is required."; return; }
        var customJobLocation = CustomJobSaveLocationBox.Text?.Trim();
        var newLocation = string.IsNullOrEmpty(customJobLocation) ? null : customJobLocation;

        UserProfile? saved;
        string? previousRoot = null;
        if (_selectedId == 0)
        {
            saved = _repo!.Add(new UserProfile
            {
                UserName = name,
                DefaultTemplateSaveLocation = string.Empty,
                CustomJobSaveLocation = newLocation
            });
        }
        else
        {
            saved = _repo!.GetById(_selectedId);
            if (saved != null)
            {
                // Where this user's jobs were going until now (their old location, else the
                // shared App Settings folder, else My Documents).
                using (var ctx = DatabaseInitializer.CreateContext())
                    previousRoot = JobStorageLocationResolver.ResolveForProfile(ctx, saved).PrimaryRoot;
                saved.UserName = name;
                saved.CustomJobSaveLocation = newLocation;
                _repo.Update(saved);
            }
        }
        StatusLabel.Text = "Saved.";
        LoadList();

        // The job save location changed: ask whether to move this user's existing jobs from the
        // folder they were being saved to into the new one.
        var locationChanged = newLocation != null && previousRoot != null &&
            !JobStorageLocationResolver.PathsEqual(previousRoot, newLocation);
        if (saved != null && locationChanged)
            await MigrateJobsAsync(saved.Id, saved.UserName, onlyIfFound: true, fromFolder: previousRoot);
    }

    private async Task MigrateJobsForSelectedAsync()
    {
        if (_selectedId == 0 || _repo!.GetById(_selectedId) is not UserProfile profile)
        {
            StatusLabel.Text = "Select a user profile first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(profile.CustomJobSaveLocation))
        {
            StatusLabel.Text = "Set and save a Job Save Location first.";
            return;
        }
        await MigrateJobsAsync(profile.Id, profile.UserName, onlyIfFound: false);
    }

    /// <summary>
    /// Finds the user's job folders on this computer's local drives, confirms, then moves them
    /// into their job save location (see <see cref="JobFolderMigrationService"/>).
    /// </summary>
    /// <param name="onlyIfFound">When true, only notes it in the status line if there is nothing to move.</param>
    /// <param name="fromFolder">
    /// Search only this folder (the previous job save location); null searches this computer's local drives.
    /// </param>
    private async Task MigrateJobsAsync(int profileId, string userName, bool onlyIfFound, string? fromFolder = null)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var service = new JobFolderMigrationService(DatabaseInitializer.CreateContext);
        JobFolderMigrationPlan plan;
        try
        {
            StatusLabel.Text = fromFolder != null
                ? $"Saved. Looking for jobs in {fromFolder}..."
                : "Looking for job folders on this computer...";
            plan = await Task.Run(() => service.Preview(profileId, fromFolder));
        }
        catch (System.Exception ex)
        {
            StatusLabel.Text = string.Empty;
            await DialogHelper.ShowInfoAsync(window, $"Could not look for job folders: {ex.Message}", "Move Jobs");
            return;
        }

        StatusLabel.Text = string.Empty;
        if (!plan.HasChanges)
        {
            if (onlyIfFound)
                StatusLabel.Text = fromFolder != null ? $"Saved. No existing jobs to move from {fromFolder}." : "Saved.";
            else
                await DialogHelper.ShowScrollableInfoAsync(window, JobFolderMigrationService.DescribeNothingToMove(plan, userName), "Move Jobs");
            return;
        }

        if (!await DialogHelper.ConfirmAsync(window, JobFolderMigrationService.DescribePlan(plan, userName), "Move Existing Jobs?"))
        {
            StatusLabel.Text = "Saved. Existing jobs were left where they are.";
            return;
        }

        MigrateJobsButton.IsEnabled = false;
        StatusLabel.Text = $"Moving {plan.Moves.Count} job folder(s)...";
        try
        {
            var result = await Task.Run(() => service.Apply(plan));
            StatusLabel.Text = $"Moved {result.JobsMoved} of {plan.Moves.Count} job folder(s).";
            await DialogHelper.ShowScrollableInfoAsync(window, JobFolderMigrationService.DescribeResult(result, plan), "Move Jobs");
        }
        catch (System.Exception ex)
        {
            StatusLabel.Text = string.Empty;
            await DialogHelper.ShowInfoAsync(window, $"Moving jobs failed: {ex.Message}", "Move Jobs");
        }
        finally
        {
            MigrateJobsButton.IsEnabled = true;
        }
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
