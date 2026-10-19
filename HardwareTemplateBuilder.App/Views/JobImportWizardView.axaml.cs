using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Step-through wizard that imports Jobs and their associated Hardware from a legacy SQLite
/// database into the master database, one job at a time. Jobs already identical to the master
/// database are skipped automatically; for everything else the user sees exactly what's new
/// (job fields, new Customer/Project Manager, hardware lines, new Manufacturer/Description/
/// HardwareItem records that would be created) and can Accept or Skip each job individually.
/// </summary>
public partial class JobImportWizardView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    private JobImportWizardService? _service;
    private JobImportWizardPlan? _plan;
    private string? _pendingSqlitePath;
    private int _currentIndex;
    private int _acceptedCount;
    private int _skippedCount;
    private int _hardwareLinksAdded;

    private sealed record UserFilterOption(int? UserProfileId, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>Initializes the wizard.</summary>
    public JobImportWizardView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        BrowseButton.Click += async (_, _) => await BrowseAndLoadUsersAsync();
        ContinueAfterUserFilterButton.Click += async (_, _) => await RunPreviewWithSelectedUserAsync();
        StartReviewButton.Click += (_, _) => ShowCurrentCandidate();
        AcceptButton.Click += async (_, _) => await AcceptCurrentAsync();
        SkipButton.Click += (_, _) => SkipCurrent();
        DoneButton.Click += (_, _) => NavigationRequested?.Invoke("Jobs");
    }

    private async Task BrowseAndLoadUsersAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Legacy SQLite Database to Compare",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Database") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" } },
                FilePickerFileTypes.All
            }
        });
        if (files == null || files.Count == 0) return;

        _pendingSqlitePath = files[0].Path.LocalPath;
        _service = new JobImportWizardService(DatabaseInitializer.CreateContext);
        StatusLabel.Text = string.Empty;

        try
        {
            var users = await _service.GetSourceUsersAsync(_pendingSqlitePath);
            var options = new List<UserFilterOption> { new(null, "All Users") };
            options.AddRange(users.Select(u => new UserFilterOption(u.Id, u.UserName)));
            UserFilterComboBox.ItemsSource = options;
            UserFilterComboBox.SelectedIndex = 0;
            UserFilterPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error reading legacy database: {ex.Message}";
        }
    }

    private async Task RunPreviewWithSelectedUserAsync()
    {
        if (_service == null || _pendingSqlitePath == null) return;

        var selected = UserFilterComboBox.SelectedItem as UserFilterOption;
        StatusLabel.Text = string.Empty;

        try
        {
            _plan = await _service.PreviewAsync(_pendingSqlitePath, selected?.UserProfileId);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error reading legacy database: {ex.Message}";
            return;
        }

        _currentIndex = 0;
        _acceptedCount = 0;
        _skippedCount = 0;
        _hardwareLinksAdded = 0;

        PickFilePanel.IsVisible = false;

        if (_plan.Candidates.Count == 0)
        {
            ShowFinish($"No jobs need review — {_plan.AlreadyUpToDateCount} job(s) in the legacy database already match the master database exactly.");
            return;
        }

        SummaryPanel.IsVisible = true;
        SummaryText.Text =
            $"{_plan.Candidates.Count} job(s) need review ({_plan.AlreadyUpToDateCount} already up to date and skipped automatically).\n\n" +
            "You'll be shown each job one at a time, along with any new hardware lines and any new " +
            "Customer, Project Manager, Manufacturer, Description, or Hardware Item records that " +
            "would be created. Nothing is written until you click \"Accept This Job\".";
    }

    private void ShowCurrentCandidate()
    {
        SummaryPanel.IsVisible = false;

        if (_plan == null || _currentIndex >= _plan.Candidates.Count)
        {
            ShowFinish(BuildFinishSummary());
            return;
        }

        ReviewPanel.IsVisible = true;
        var candidate = _plan.Candidates[_currentIndex];

        ProgressLabel.Text = $"Job {_currentIndex + 1} of {_plan.Candidates.Count}";
        JobHeaderText.Text = $"{candidate.JobNumber} — {candidate.JobName}";

        JobStatusText.Text = candidate.Status == JobCandidateStatus.Missing
            ? $"This job does not exist in the master database yet. Customer: {candidate.CustomerName}  ·  Project Manager: {candidate.ProjectManagerName}"
            : $"This job already exists in the master database but is missing {candidate.HardwareLines.Count} hardware line(s). Customer: {candidate.CustomerName}  ·  Project Manager: {candidate.ProjectManagerName}";

        var newRecordNotes = new List<string>();
        if (candidate.CustomerIsNew) newRecordNotes.Add($"new Customer \"{candidate.CustomerName}\"");
        if (candidate.ProjectManagerIsNew) newRecordNotes.Add($"new Project Manager \"{candidate.ProjectManagerName}\"");
        var newHwCount = candidate.HardwareLines.Count(l => l.HardwareItemIsNew);
        if (newHwCount > 0) newRecordNotes.Add($"{newHwCount} new Hardware Item record(s) (with Manufacturer/Description created as needed)");

        JobNewRecordsText.Text = newRecordNotes.Count > 0
            ? "Will create: " + string.Join(", ", newRecordNotes)
            : string.Empty;

        HardwareLinesList.ItemsSource = candidate.HardwareLines
            .Select(l => l.DisplayLabel + (l.HardwareItemIsNew ? "  [NEW]" : string.Empty))
            .ToList();
    }

    private async Task AcceptCurrentAsync()
    {
        if (_service == null || _plan == null || _currentIndex >= _plan.Candidates.Count) return;

        var candidate = _plan.Candidates[_currentIndex];
        AcceptButton.IsEnabled = false;
        SkipButton.IsEnabled = false;

        try
        {
            var result = await _service.ApplyJobAsync(candidate);
            _acceptedCount++;
            _hardwareLinksAdded += result.HardwareLinksAdded;
            StatusLabel.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error importing job {candidate.JobNumber}: {ex.Message}";
        }
        finally
        {
            AcceptButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
        }

        _currentIndex++;
        ShowCurrentCandidate();
    }

    private void SkipCurrent()
    {
        _skippedCount++;
        _currentIndex++;
        ShowCurrentCandidate();
    }

    private string BuildFinishSummary() =>
        $"Review complete.\n\n" +
        $"{_acceptedCount} job(s) imported, {_skippedCount} job(s) skipped, {_hardwareLinksAdded} hardware link(s) added.";

    private void ShowFinish(string message)
    {
        ReviewPanel.IsVisible = false;
        SummaryPanel.IsVisible = false;
        FinishPanel.IsVisible = true;
        FinishSummaryText.Text = message;
    }
}
