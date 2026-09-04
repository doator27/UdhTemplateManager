using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using HardwareTemplateBuilder.App;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// List and metadata-editor view for <see cref="Job"/> records.
/// Select a job and click "Open Job" to navigate to <see cref="JobDetailView"/>.
/// </summary>
public partial class JobsView : UserControl
{
    /// <summary>
    /// Lightweight wrapper used as the ListBox item type so the row template can
    /// apply red colouring when a note has been outstanding for more than one week.
    /// </summary>
    private sealed class JobRow
    {
        public Job Job { get; }
        public string CreatorName { get; }
        public string Display => Job.JobNumber + (string.IsNullOrWhiteSpace(Job.JobName) ? "" : $"  —  {Job.JobName}");

        /// <summary>
        /// True when the job has an unresolved note that is more than 7 days old.
        /// </summary>
        public bool IsOverdue =>
            !string.IsNullOrWhiteSpace(Job.Notes) &&
            Job.NotesUpdatedAt.HasValue &&
            (DateTime.UtcNow - Job.NotesUpdatedAt.Value).TotalDays > 7;

        public JobRow(Job job, string creatorName) { Job = job; CreatorName = creatorName; }
    }

    private List<Customer> _customers = new();
    private List<ProjectManager> _projectManagers = new();
    private List<UserProfile> _userProfiles = new();
    private int _selectedJobId;
    private bool _selectedJobIsComplete;
    private DateTime _lastRefreshed = DateTime.MinValue;
    /// <summary>Null = default job-number sort; true = creator ascending; false = creator descending.</summary>
    private bool? _creatorSort = null;

    /// <summary>Raised when the user requests navigation to a named view (e.g. "JobDetail:42").</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public JobsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        LoadCustomers();
        LoadProjectManagers();
        LoadUserProfiles();
        LoadJobList();

        // Set the ListBox item template once — rows with overdue notes render in dark red.
        JobList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<JobRow>((row, _) =>
        {
            var tb = new TextBlock
            {
                Text     = row?.Display ?? "",
                Padding  = new Avalonia.Thickness(2),
                Foreground = (row?.IsOverdue == true)
                    ? AppColors.Danger
                    : AppColors.Primary,
            };
            return tb;
        }, supportsRecycling: false);

        FilterBox.TextChanged += (_, _) => LoadJobList();
        ShowCompletedCheck.IsCheckedChanged += (_, _) => LoadJobList();
        CreatorFilterCombo.SelectionChanged += (_, _) => LoadJobList();
        SortByCreatorButton.Click += (_, _) =>
        {
            _creatorSort = _creatorSort switch
            {
                null  => true,   // job# → creator ↑
                true  => false,  // creator ↑ → creator ↓
                false => null    // creator ↓ → job#
            };
            SortByCreatorButton.Content = _creatorSort switch
            {
                true  => "Sort: Creator ↑",
                false => "Sort: Creator ↓",
                null  => "Sort: Job # ↑"
            };
            LoadJobList();
        };
        JobList.SelectionChanged += (_, _) => OnJobSelected();
        SaveButton.Click += async (_, _) => await SaveAsync();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this job?"))
                DeleteSelected();
        };
        OpenJobButton.Click += (_, _) =>
        {
            if (_selectedJobId == 0) { StatusLabel.Text = "Select a job to open."; return; }
            NavigationRequested?.Invoke($"JobDetail:{_selectedJobId}");
        };
        ToggleCompleteButton.Click += (_, _) => ToggleJobComplete();
        SaveNotesButton.Click  += (_, _) => SaveNotes();
        ClearNotesButton.Click += (_, _) => ClearNotes();

        AddCustomerButton.Click        += (_, _) => TogglePanel(NewCustomerPanel, NewCustomerBox);
        CancelCustomerButton.Click     += (_, _) => HidePanel(NewCustomerPanel, NewCustomerBox);
        SaveCustomerButton.Click       += (_, _) => AddCustomer();

        AddProjectManagerButton.Click      += (_, _) => TogglePanel(NewProjectManagerPanel, NewProjectManagerBox);
        CancelProjectManagerButton.Click   += (_, _) => HidePanel(NewProjectManagerPanel, NewProjectManagerBox);
        SaveProjectManagerButton.Click     += (_, _) => AddProjectManager();

        // Auto-refresh when the window regains focus so changes made by other users on the
        // shared database are visible without a manual reload. Only reloads if the view has
        // been inactive for at least 30 seconds to avoid redundant queries during normal use.
        if (TopLevel.GetTopLevel(this) is Window parentWindow)
            parentWindow.Activated += (_, _) => RefreshIfStale();
    }

    /// <summary>
    /// Reloads the job list if the view has been inactive for more than 30 seconds.
    /// Called when the parent window regains focus.
    /// </summary>
    private void RefreshIfStale()
    {
        if ((DateTime.UtcNow - _lastRefreshed).TotalSeconds < 30) return;
        LoadJobList();
    }

    private void LoadUserProfiles()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _userProfiles = new UserProfileRepository(ctx).GetAll().OrderBy(p => p.UserName).ToList();

        // Populate creator filter: "(Any)" sentinel at the top, then each profile by name.
        var items = new List<UserProfile?> { null }.Concat(_userProfiles.Cast<UserProfile?>()).ToList();
        CreatorFilterCombo.ItemsSource = items;
        CreatorFilterCombo.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");
        var activeId = SessionService.ActiveUserProfile?.Id;
        int selectIndex = 0;
        if (activeId.HasValue)
        {
            var idx = items.FindIndex(p => p?.Id == activeId.Value);
            if (idx >= 0) selectIndex = idx;
        }
        CreatorFilterCombo.SelectedIndex = selectIndex;
    }

    /// <summary>
    /// Re-syncs the creator filter combo to <see cref="SessionService.ActiveUserProfile"/>.
    /// Called by the host window once the active user is resolved, since that resolution
    /// happens asynchronously and may complete after this view has already loaded.
    /// </summary>
    public void RefreshActiveUserSelection() => LoadUserProfiles();

    private void LoadCustomers(Customer? selectAfter = null)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _customers = new CustomerRepository(ctx).GetAll().OrderBy(c => c.CustomerName).ToList();
        CustomerCombo.ItemsSource = _customers;
        CustomerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("CustomerName");
        if (selectAfter != null)
            CustomerCombo.SelectedItem = _customers.FirstOrDefault(c => c.Id == selectAfter.Id);
    }

    private void LoadProjectManagers(ProjectManager? selectAfter = null)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _projectManagers = new ProjectManagerRepository(ctx).GetAll().OrderBy(pm => pm.ProjectManagerName).ToList();
        ProjectManagerCombo.ItemsSource = _projectManagers;
        ProjectManagerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ProjectManagerName");
        if (selectAfter != null)
            ProjectManagerCombo.SelectedItem = _projectManagers.FirstOrDefault(pm => pm.Id == selectAfter.Id);
    }

    private void AddCustomer()
    {
        var name = NewCustomerBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter a customer name."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var saved = new CustomerRepository(ctx).Add(new Customer { CustomerName = name });
        HidePanel(NewCustomerPanel, NewCustomerBox);
        LoadCustomers(saved);
        StatusLabel.Text = $"Customer \"{saved.CustomerName}\" added.";
    }

    private void AddProjectManager()
    {
        var name = NewProjectManagerBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter a project manager name."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var saved = new ProjectManagerRepository(ctx).Add(new ProjectManager { ProjectManagerName = name });
        HidePanel(NewProjectManagerPanel, NewProjectManagerBox);
        LoadProjectManagers(saved);
        StatusLabel.Text = $"Project manager \"{saved.ProjectManagerName}\" added.";
    }

    private static void TogglePanel(Avalonia.Controls.Control panel, TextBox box)
    {
        panel.IsVisible = !panel.IsVisible;
        if (panel.IsVisible) box.Focus();
    }

    private static void HidePanel(Avalonia.Controls.Control panel, TextBox box)
    {
        panel.IsVisible = false;
        box.Text = "";
    }

    /// <summary>
    /// Reloads the job list. When <paramref name="selectJobId"/> is given, the matching row is
    /// selected and scrolled into view afterward — used after Save() so a newly created or
    /// edited job is immediately visible instead of requiring the user to search for it.
    /// </summary>
    private void LoadJobList(int? selectJobId = null)
    {
        var filter           = FilterBox.Text?.ToLower() ?? "";
        var showComplete     = ShowCompletedCheck.IsChecked == true;
        var creatorFilter    = CreatorFilterCombo.SelectedItem as UserProfile;

        using var ctx = DatabaseInitializer.CreateContext();
        var jobs = new JobRepository(ctx).GetAll()
            .Where(j => showComplete || !j.IsComplete)
            .Where(j => string.IsNullOrEmpty(filter) ||
                        j.JobNumber.ToLower().Contains(filter) ||
                        j.JobName.ToLower().Contains(filter))
            .Where(j => creatorFilter == null || j.UserProfileId == creatorFilter.Id)
            .ToList();

        var unsorted = jobs.Select(j => new JobRow(j,
            _userProfiles.FirstOrDefault(p => p.Id == j.UserProfileId)?.UserName ?? ""));

        IEnumerable<JobRow> rows = _creatorSort switch
        {
            true  => unsorted.OrderBy(r => r.CreatorName).ThenBy(r => r.Job.JobNumber),
            false => unsorted.OrderByDescending(r => r.CreatorName).ThenBy(r => r.Job.JobNumber),
            _     => unsorted.OrderBy(r => r.Job.JobNumber)
        };

        var rowList = rows.ToList();
        JobList.ItemsSource = rowList;
        _lastRefreshed = DateTime.UtcNow;

        if (selectJobId.HasValue)
        {
            var match = rowList.FirstOrDefault(r => r.Job.Id == selectJobId.Value);
            if (match != null)
            {
                JobList.SelectedItem = match;
                JobList.ScrollIntoView(match);
                JobList.Focus();
            }
        }
    }

    private void OnJobSelected()
    {
        if (JobList.SelectedItem is not JobRow row) return;
        var j = row.Job;

        _selectedJobId         = j.Id;
        _selectedJobIsComplete = j.IsComplete;
        JobNumberBox.Text  = j.JobNumber;
        JobNameBox.Text    = j.JobName;
        CustomerCombo.SelectedItem       = _customers.FirstOrDefault(c => c.Id == j.CustomerId);
        ProjectManagerCombo.SelectedItem = _projectManagers.FirstOrDefault(pm => pm.Id == j.ProjectManagerId);
        ToggleCompleteButton.Content = j.IsComplete ? "↺ Reactivate" : "✓ Mark Complete";
        StatusLabel.Text = j.IsComplete ? "[Complete]" : "";

        NotesBox.Text         = j.Notes ?? "";
        NotesStatusLabel.Text = "";
        NotesDateLabel.Text   = j.NotesUpdatedAt.HasValue
            ? $"Saved {j.NotesUpdatedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "";
        NotesDateLabel.Foreground = (row.IsOverdue)
            ? AppColors.Danger
            : AppColors.Muted;

    }

    private void ToggleJobComplete()
    {
        if (_selectedJobId == 0) { StatusLabel.Text = "Select a job first."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_selectedJobId);
        if (job == null) return;
        job.IsComplete = !job.IsComplete;
        ctx.SaveChanges();
        _selectedJobIsComplete = job.IsComplete;
        ToggleCompleteButton.Content = job.IsComplete ? "↺ Reactivate" : "✓ Mark Complete";
        StatusLabel.Text = job.IsComplete ? "Marked complete." : "Reactivated.";
        LoadJobList();
    }

    private async Task SaveAsync()
    {
        var jobNumber = JobNumberBox.Text?.Trim();
        var jobName = JobNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(jobNumber)) { StatusLabel.Text = "Job Number is required."; return; }
        if (string.IsNullOrEmpty(jobName)) { StatusLabel.Text = "Job Name is required."; return; }
        if (CustomerCombo.SelectedItem is not Customer customer) { StatusLabel.Text = "Customer is required."; return; }
        if (ProjectManagerCombo.SelectedItem is not ProjectManager pm) { StatusLabel.Text = "Project Manager is required."; return; }

        var userProfileId = SessionService.ActiveUserProfile?.Id ?? 0;
        int savedJobId;

        if (_selectedJobId == 0)
        {
            using (var checkCtx = DatabaseInitializer.CreateContext())
            {
                var duplicate = checkCtx.Jobs.FirstOrDefault(j => j.JobNumber == jobNumber);
                if (duplicate != null)
                {
                    var window = TopLevel.GetTopLevel(this) as Window;
                    bool createRelease = window != null && await DialogHelper.ConfirmAsync(window,
                        $"A job with number '{jobNumber}' already exists (\"{duplicate.JobName}\"). " +
                        "Would you like to create a new Release under that job instead of creating a duplicate job?",
                        "Duplicate Job Number");

                    if (createRelease)
                    {
                        StatusLabel.Text = "Opening existing job to add a release…";
                        _selectedJobId = duplicate.Id;
                        LoadJobList(duplicate.Id);
                        NavigationRequested?.Invoke($"JobDetail:{duplicate.Id}");
                        return;
                    }
                }
            }

            using var ctx = DatabaseInitializer.CreateContext();
            var newJob = new JobRepository(ctx).Add(new Job
            {
                JobNumber        = jobNumber,
                JobName          = jobName,
                CustomerId       = customer.Id,
                ProjectManagerId = pm.Id,
                UserProfileId    = userProfileId
            });
            savedJobId = newJob.Id;
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var repo = new JobRepository(ctx);
            var existing = repo.GetById(_selectedJobId);
            if (existing != null)
            {
                existing.JobNumber        = jobNumber;
                existing.JobName          = jobName;
                existing.CustomerId       = customer.Id;
                existing.ProjectManagerId = pm.Id;
                repo.Update(existing);
            }
            savedJobId = _selectedJobId;
        }
        StatusLabel.Text = "Saved.";
        _selectedJobId = savedJobId;
        LoadJobList(savedJobId);
    }

    private void DeleteSelected()
    {
        if (_selectedJobId == 0) return;
        using var ctx = DatabaseInitializer.CreateContext();
        new JobRepository(ctx).Delete(_selectedJobId);
        ClearForm();
        LoadJobList();
    }

    private void SaveNotes()
    {
        if (_selectedJobId == 0) { NotesStatusLabel.Text = "Select a job first."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_selectedJobId);
        if (job == null) return;

        var text = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();
        job.Notes          = text;
        job.NotesUpdatedAt = text != null ? DateTime.UtcNow : null;
        ctx.SaveChanges();

        var savedAt = job.NotesUpdatedAt?.ToLocalTime();
        NotesDateLabel.Text      = savedAt.HasValue ? $"Saved {savedAt.Value:yyyy-MM-dd HH:mm}" : "";
        NotesDateLabel.Foreground = AppColors.Muted;
        NotesStatusLabel.Foreground = AppColors.Success;
        NotesStatusLabel.Text    = "Saved.";
        LoadJobList();
    }

    private void ClearNotes()
    {
        if (_selectedJobId == 0) { NotesStatusLabel.Text = "Select a job first."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_selectedJobId);
        if (job == null) return;

        job.Notes          = null;
        job.NotesUpdatedAt = null;
        ctx.SaveChanges();

        NotesBox.Text             = "";
        NotesDateLabel.Text       = "";
        NotesStatusLabel.Foreground = AppColors.Success;
        NotesStatusLabel.Text     = "Note cleared.";
        LoadJobList();
    }

    private void ClearForm()
    {
        _selectedJobId = 0;
        JobNumberBox.Text = "";
        JobNameBox.Text   = "";
        CustomerCombo.SelectedItem       = null;
        ProjectManagerCombo.SelectedItem = null;
        StatusLabel.Text      = "";
        NotesBox.Text         = "";
        NotesDateLabel.Text   = "";
        NotesStatusLabel.Text = "";
        JobList.SelectedItem  = null;

    }
}
