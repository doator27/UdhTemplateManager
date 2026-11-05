using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Maintenance view that lets the user search jobs by any job field, select one or more jobs
/// via checkboxes (with a select-all/select-none option), and transfer ownership of the
/// selected jobs to a different user in a single operation.
/// </summary>
public partial class TransferJobView : UserControl
{
    /// <summary>Row wrapper for the job list — pairs a <see cref="Job"/> with its display text.</summary>
    private sealed class TransferJobRow
    {
        public Job Job { get; }

        public string Display =>
            $"{Job.JobNumber} — {Job.JobName}  |  {Job.Customer?.CustomerName}  |  PM: {Job.ProjectManager?.ProjectManagerName}  |  Owner: {Job.UserProfile?.UserName}" +
            (Job.IsComplete ? "  (inactive)" : string.Empty);

        public TransferJobRow(Job job)
        {
            Job = job;
        }
    }

    /// <summary>
    /// Selected job IDs. Kept separate from the row objects — reloading the list (e.g. filtering)
    /// creates new row instances; a HashSet keyed by Job.Id survives that reload.
    /// </summary>
    private readonly HashSet<int> _selectedJobIds = new();

    private List<TransferJobRow> _currentRows = new();
    private List<UserProfile> _userProfiles = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public TransferJobView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        JobList.ItemTemplate = new FuncDataTemplate<TransferJobRow>((row, _) =>
        {
            var checkBox = new CheckBox
            {
                Content = row?.Display ?? string.Empty,
                IsChecked = row != null && _selectedJobIds.Contains(row.Job.Id)
            };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (row == null) return;
                if (checkBox.IsChecked == true) _selectedJobIds.Add(row.Job.Id);
                else _selectedJobIds.Remove(row.Job.Id);
                UpdateSelectedCountLabel();
            };
            return checkBox;
        }, supportsRecycling: false); // false is deliberate — with recycling, a reused container's
                                       // checked state can desync from the underlying row on scroll.

        FilterBox.TextChanged += (_, _) => LoadJobList();
        IncludeInactiveCheck.IsCheckedChanged += (_, _) => LoadJobList();
        OwnerFilterCombo.SelectionChanged += (_, _) => LoadJobList();
        SelectAllButton.Click += (_, _) => SetSelection(_currentRows.Select(r => r.Job.Id), selected: true);
        SelectNoneButton.Click += (_, _) => SetSelection(_currentRows.Select(r => r.Job.Id), selected: false);
        TransferButton.Click += async (_, _) => await OnTransferAsync();

        LoadUserProfiles();
        LoadJobList();
    }

    // ---------- Setup ----------

    /// <summary>Populates the owner filter and transfer-to combos with all user profiles.</summary>
    private void LoadUserProfiles()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _userProfiles = new UserProfileRepository(ctx).GetAll().OrderBy(p => p.UserName).ToList();

        var filterItems = new List<UserProfile?> { null }.Concat(_userProfiles.Cast<UserProfile?>()).ToList();
        OwnerFilterCombo.ItemsSource = filterItems;
        OwnerFilterCombo.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");
        OwnerFilterCombo.SelectedIndex = 0;

        TransferToCombo.ItemsSource = _userProfiles;
        TransferToCombo.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");
    }

    // ---------- Job list ----------

    /// <summary>
    /// Reloads the job list from the database, applying the free-text search (across all job
    /// fields) and the owner/active filters.
    /// </summary>
    private void LoadJobList()
    {
        var includeInactive = IncludeInactiveCheck.IsChecked == true;
        var ownerFilter = OwnerFilterCombo.SelectedItem as UserProfile;
        var filterText = FilterBox.Text?.Trim();

        using var context = DatabaseInitializer.CreateContext();
        var jobs = context.Jobs
            .Include(j => j.Customer)
            .Include(j => j.ProjectManager)
            .Include(j => j.UserProfile)
            .Where(j => includeInactive || !j.IsComplete)
            .Where(j => ownerFilter == null || j.UserProfileId == ownerFilter.Id)
            .ToList();

        if (!string.IsNullOrWhiteSpace(filterText))
        {
            jobs = jobs.Where(j =>
                Contains(j.JobNumber, filterText) ||
                Contains(j.JobName, filterText) ||
                Contains(j.Customer?.CustomerName, filterText) ||
                Contains(j.ProjectManager?.ProjectManagerName, filterText) ||
                Contains(j.UserProfile?.UserName, filterText) ||
                Contains(j.Notes, filterText) ||
                Contains(j.IsComplete ? "complete" : "active", filterText)
            ).ToList();
        }

        _currentRows = jobs
            .OrderBy(j => j.JobNumber)
            .Select(j => new TransferJobRow(j))
            .ToList();

        // Drop selections for jobs that fell out of the current filter so the count stays accurate.
        var visibleIds = _currentRows.Select(r => r.Job.Id).ToHashSet();
        _selectedJobIds.RemoveWhere(id => !visibleIds.Contains(id));

        RefreshJobListDisplay();
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Forces the ListBox to rebuild its item containers so checkbox states reflect <see cref="_selectedJobIds"/>.</summary>
    private void RefreshJobListDisplay()
    {
        JobList.ItemsSource = null;
        JobList.ItemsSource = _currentRows;
        UpdateSelectedCountLabel();
    }

    private void SetSelection(IEnumerable<int> jobIds, bool selected)
    {
        foreach (var id in jobIds)
        {
            if (selected) _selectedJobIds.Add(id);
            else _selectedJobIds.Remove(id);
        }
        RefreshJobListDisplay();
    }

    private void UpdateSelectedCountLabel() =>
        SelectedCountLabel.Text = $"{_selectedJobIds.Count} of {_currentRows.Count} selected";

    // ---------- Transfer ----------

    private async System.Threading.Tasks.Task OnTransferAsync()
    {
        StatusLabel.Text = string.Empty;

        if (_selectedJobIds.Count == 0)
        {
            StatusLabel.Text = "Select at least one job first.";
            return;
        }

        var destination = TransferToCombo.SelectedItem as UserProfile;
        if (destination == null)
        {
            StatusLabel.Text = "Select a user to transfer the selected job(s) to.";
            return;
        }

        var win = TopLevel.GetTopLevel(this) as Window;
        if (win != null)
        {
            var confirmed = await DialogHelper.ConfirmAsync(
                win,
                $"Transfer {_selectedJobIds.Count} job(s) to {destination.UserName}?",
                "Transfer Job");
            if (!confirmed) return;
        }

        var jobIds = _selectedJobIds.ToList();

        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var repo = new JobRepository(ctx);
            var jobs = ctx.Jobs.Where(j => jobIds.Contains(j.Id)).ToList();
            foreach (var job in jobs)
            {
                job.UserProfileId = destination.Id;
                repo.Update(job);
            }
        }

        StatusLabel.Text = $"{jobIds.Count} job(s) transferred to {destination.UserName}.";
        _selectedJobIds.Clear();
        LoadJobList();
    }
}
