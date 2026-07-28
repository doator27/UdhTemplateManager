using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using HardwareTemplateBuilder.App;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Lets the user select several jobs at once and generate all their PDF template packages in
/// one run. Selection is one-off per visit — nothing is persisted between sessions. A failing
/// job is skipped (not fatal to the batch) and reported in the final summary.
/// </summary>
public partial class BatchGenerateView : UserControl
{
    /// <summary>Row wrapper for the job list — pairs a <see cref="Job"/> with its last-generated date.</summary>
    private sealed class BatchJobRow
    {
        public Job Job { get; }

        /// <summary>Null means the job has never had a package generated.</summary>
        public DateTime? LastGenerated { get; }

        public string Display => $"{Job.JobNumber} — {Job.JobName}" +
            (LastGenerated is { } d ? $"  (last generated {d:yyyy-MM-dd})" : "  (never generated)");

        public BatchJobRow(Job job, DateTime? lastGenerated)
        {
            Job = job;
            LastGenerated = lastGenerated;
        }
    }

    /// <summary>
    /// Selected job IDs. Kept separate from the row objects (rather than a mutable bool on
    /// <see cref="BatchJobRow"/>) because reloading the list — e.g. toggling "include inactive" —
    /// creates new row instances; a HashSet keyed by Job.Id survives that reload.
    /// </summary>
    private readonly HashSet<int> _selectedJobIds = new();

    private List<BatchJobRow> _currentRows = new();
    private List<UserProfile> _userProfiles = new();
    private CancellationTokenSource? _batchCts;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public BatchGenerateView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        JobList.ItemTemplate = new FuncDataTemplate<BatchJobRow>((row, _) =>
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

        IncludeInactiveCheck.IsCheckedChanged += (_, _) => LoadJobList();
        CreatorFilterCombo.SelectionChanged += (_, _) => LoadJobList();
        SelectAllButton.Click += (_, _) => SetSelection(_currentRows.Select(r => r.Job.Id), selected: true);
        SelectNoneButton.Click += (_, _) => SetSelection(_currentRows.Select(r => r.Job.Id), selected: false);
        StartButton.Click += async (_, _) => await OnStartBatchAsync();
        CancelButton.Click += (_, _) => _batchCts?.Cancel();

        LoadUserProfiles();
        LoadJobList();
    }

    // ---------- Job list ----------

    /// <summary>
    /// Populates the creator filter combo: "(Any)" sentinel at the top, then each user profile
    /// by name, defaulting the selection to the active session user.
    /// </summary>
    private void LoadUserProfiles()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _userProfiles = new UserProfileRepository(ctx).GetAll().OrderBy(p => p.UserName).ToList();

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

    private void LoadJobList()
    {
        var includeInactive = IncludeInactiveCheck.IsChecked == true;
        var creatorFilter = CreatorFilterCombo.SelectedItem as UserProfile;

        using var context = DatabaseInitializer.CreateContext();
        var jobs = context.Jobs
            .Include(j => j.Snapshots)
            .Where(j => includeInactive || !j.IsComplete)
            .Where(j => creatorFilter == null || j.UserProfileId == creatorFilter.Id)
            .ToList();

        _currentRows = jobs
            .Select(j => new BatchJobRow(
                j,
                j.Snapshots.Count > 0 ? j.Snapshots.Max(s => s.SnapshotDate) : (DateTime?)null))
            .OrderBy(r => r.LastGenerated ?? DateTime.MinValue)
            .ToList();

        // Drop selections for jobs that fell out of the current filter (e.g. "include inactive"
        // was unchecked) so the count stays accurate.
        var visibleIds = _currentRows.Select(r => r.Job.Id).ToHashSet();
        _selectedJobIds.RemoveWhere(id => !visibleIds.Contains(id));

        RefreshJobListDisplay();
    }

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

    // ---------- Batch run ----------

    private async Task OnStartBatchAsync()
    {
        if (_selectedJobIds.Count == 0)
        {
            StatusLabel.Text = "Select at least one job first.";
            return;
        }

        var jobIds = _selectedJobIds.ToList();

        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        SetRunningState(running: true);
        StatusLabel.Text = string.Empty;
        ResultsBorder.IsVisible = false;
        var resultRows = new List<string>();
        ResultsList.ItemsSource = resultRows;

        var progress = new Progress<BatchGenerationProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            ProgressText.Text = $"Job {p.CurrentIndex} of {p.TotalJobs} — {p.JobNumber}: {p.StatusMessage}";
            BatchProgressBar.Maximum = p.TotalJobs;
            BatchProgressBar.Value = p.CurrentIndex;

            if (p.CompletedJob is { } job)
            {
                resultRows.Add(job.Success
                    ? $"✓ {job.JobNumber} — {job.JobName}"
                    : $"✗ {job.JobNumber} — {job.JobName}: {job.ErrorMessage}");
                ResultsList.ItemsSource = null;
                ResultsList.ItemsSource = resultRows;
                ResultsBorder.IsVisible = true;
            }
        }));

        BatchGenerationResult result;
        try
        {
            var socketsHandler = new System.Net.Http.SocketsHttpHandler();
            socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var httpClient = new HttpClient(socketsHandler);
            var service = new BatchJobPackageGenerationService(DatabaseInitializer.CreateContext, httpClient);

            result = await Task.Run(() => service.RunBatchAsync(jobIds, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Batch cancelled.";
            SetRunningState(running: false);
            return;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Unexpected error: {ex.Message}";
            SetRunningState(running: false);
            return;
        }

        SetRunningState(running: false);
        StatusLabel.Text = $"{result.SuccessCount} of {result.TotalJobs} job(s) generated successfully.";

        var win = TopLevel.GetTopLevel(this) as Window;
        if (win != null)
            await DialogHelper.ShowScrollableInfoAsync(win, BuildSummaryReport(result), "Batch Generate — Summary");

        LoadJobList();
    }

    private static string BuildSummaryReport(BatchGenerationResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{result.SuccessCount} of {result.TotalJobs} job(s) generated successfully.");
        sb.AppendLine();

        var failed = result.Jobs.Where(j => !j.Success).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine($"Failed ({failed.Count}):");
            foreach (var job in failed)
            {
                sb.AppendLine($"• {job.JobNumber} — {job.JobName}");
                sb.AppendLine($"  {job.ErrorMessage}");
                sb.AppendLine();
            }
        }

        var succeeded = result.Jobs.Where(j => j.Success).ToList();
        if (succeeded.Count > 0)
        {
            sb.AppendLine($"Succeeded ({succeeded.Count}):");
            foreach (var job in succeeded)
                sb.AppendLine($"• {job.JobNumber} — {job.JobName}");
        }

        return sb.ToString().TrimEnd();
    }

    private void SetRunningState(bool running)
    {
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        ProgressPanel.IsVisible = running;
        IncludeInactiveCheck.IsEnabled = !running;
        CreatorFilterCombo.IsEnabled = !running;
        SelectAllButton.IsEnabled = !running;
        SelectNoneButton.IsEnabled = !running;
        JobList.IsEnabled = !running;
    }
}
