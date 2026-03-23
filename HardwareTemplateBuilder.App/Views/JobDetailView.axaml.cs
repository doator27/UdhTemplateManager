using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Detail view for a single <see cref="Job"/>: manages linked hardware items and
/// generates the PDF template package. Navigated to from <see cref="JobsView"/>.
/// </summary>
public partial class JobDetailView : UserControl
{
    private readonly int _jobId;
    private JobHardwareRepository? _jobHardwareRepo;
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _descComboItems = new();

    /// <summary>The observable collection backing the linked hardware listbox, enabling drag-and-drop reorder.</summary>
    private readonly ObservableCollection<JobHardware> _linkedHardware = new();

    /// <summary>Cancellation source for any in-progress package generation.</summary>
    private CancellationTokenSource? _packageCts;

    /// <summary>The hardware row being dragged for reorder operations.</summary>
    private JobHardware? _draggedItem;

    private bool _isDragging;
    private Point _dragStartPoint;
    private bool _updatingSearchDescCombo;

    /// <summary>Currently selected release ID; null means the base hardware list.</summary>
    private int? _currentReleaseId;

    /// <summary>Tracks whether the job is currently marked complete.</summary>
    private bool _isComplete;

    /// <summary>Raised when the user requests navigation to a named view (e.g. "Jobs").</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the detail view for the given job.</summary>
    /// <param name="jobId">The ID of the job to display and manage.</param>
    public JobDetailView(int jobId)
    {
        _jobId = jobId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _jobHardwareRepo = new JobHardwareRepository(context);
        _manufacturers  = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(new DescriptionRepository(context).GetAll());

        // Show job header and completion status
        var job = context.Jobs.Find(_jobId);
        JobTitleLabel.Text = job != null ? $"Job: {job.JobNumber} — {job.JobName}" : $"Job #{_jobId}";
        _isComplete = job?.IsComplete ?? false;
        UpdateCompleteButtons();

        // Search combos
        var anyMfr = new List<Manufacturer> { new Manufacturer { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        SearchMfrCombo.ItemsSource = anyMfr;
        SearchMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        SearchMfrCombo.SelectedIndex = 0;

        var anyDesc = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        anyDesc.AddRange(_descComboItems);
        SearchDescCombo.ItemsSource = anyDesc;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        SearchDescCombo.SelectedIndex = 0;

        LinkedHardwareList.ItemsSource = _linkedHardware;
        LinkedHardwareList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");

        LoadLinkedHardware();

        // Attachments
        AttachmentList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");
        LoadAttachments();
        AddEmailButton.Click          += async (_, _) => await AddAttachmentAsync("Email");
        AddPdfButton.Click            += async (_, _) => await AddAttachmentAsync("PDF");
        OpenAttachmentButton.Click    += (_, _) => OpenAttachment();
        RemoveAttachmentButton.Click  += (_, _) => RemoveAttachment();

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        BackButton.Click += (_, _) => NavigationRequested?.Invoke("Jobs");
        BulkAddButton.Click += (_, _) => NavigationRequested?.Invoke($"BulkHardwareEntry:{_jobId}");

        // Phase 20: Mark complete / Reactivate
        MarkCompleteButton.Click  += async (_, _) => await MarkCompleteAsync();
        ReactivateButton.Click    += (_, _) => Reactivate();

        // Collapsible section toggles
        WireToggle(ToggleAttachmentsButton,     AttachmentsBody);
        WireToggle(ToggleLinkedHardwareButton,  LinkedHardwareBody);
        WireToggle(ToggleQuickCreateButton,     QuickCreateBody);
        WireToggle(TogglePdfButton,             PdfBody);

        // Phase 22: Sort + Edit
        SortByMfrButton.Click  += (_, _) => SortLinkedHardware("mfr");
        SortByDescButton.Click += (_, _) => SortLinkedHardware("desc");
        EditHardwareButton.Click += (_, _) =>
        {
            if (LinkedHardwareList.SelectedItem is not JobHardware jh)
            {
                LinkStatusLabel.Text = "Select a hardware item to edit.";
                return;
            }
            EditCustomDescBox.Text = jh.CustomDescription ?? string.Empty;
            EditHardwarePanel.IsVisible = true;
        };
        SaveHardwareEditButton.Click += (_, _) => SaveHardwareEdit();
        CancelHardwareEditButton.Click += (_, _) =>
        {
            EditHardwarePanel.IsVisible = false;
            EditHardwareStatusLabel.Text = string.Empty;
        };

        // Phase 23: Releases
        LoadReleaseCombo();
        ReleaseCombo.SelectionChanged += (_, _) => OnReleaseChanged();
        NewReleaseButton.Click    += (_, _) => { NewReleasePanel.IsVisible = true; ReleaseLabelBox.Focus(); };
        CancelReleaseButton.Click += (_, _) => { NewReleasePanel.IsVisible = false; ReleaseStatusLabel.Text = string.Empty; };
        SaveReleaseButton.Click   += (_, _) => CreateRelease();

        SearchMfrCombo.SelectionChanged += (_, _) => OnSearchMfrChanged();
        SearchDescCombo.SelectionChanged += (_, _) => { if (!_updatingSearchDescCombo) SearchHardware(); };
        SearchModelBox.TextChanged += (_, _) => SearchHardware();
        AddHardwareButton.Click += (_, _) => AddHardwareToJob();
        RemoveHardwareButton.Click += (_, _) => RemoveHardwareFromJob();
        GeneratePackageButton.Click    += async (_, _) => await OnGeneratePackageAsync();
        BrowseOldVersionsButton.Click  += (_, _) => BrowseOldVersions();

        // Quick Create toggles
        ToggleNewHardwareButton.Click    += (_, _) => TogglePanel(NewHardwarePanel,    InitNewHardwarePanel);
        ToggleNewDescriptionButton.Click += (_, _) => TogglePanel(NewDescriptionPanel, InitNewDescriptionPanel);
        ToggleNewTemplateButton.Click    += (_, _) => TogglePanel(NewTemplatePanel,    InitNewTemplatePanel);

        CreateHardwareButton.Click    += (_, _) => CreateHardwareItem();
        CreateDescriptionButton.Click += (_, _) => CreateDescription();
        CreateTemplateButton.Click    += async (_, _) => await CreateTemplateAsync();
        NewTplBrowseButton.Click      += async (_, _) => await BrowseLocalFileAsync();

        // Drag-and-drop reorder on linked hardware list
        LinkedHardwareList.AddHandler(PointerPressedEvent, OnLinkedListPointerPressed, RoutingStrategies.Tunnel);
        LinkedHardwareList.AddHandler(PointerReleasedEvent, OnLinkedListPointerReleased, RoutingStrategies.Tunnel);
        LinkedHardwareList.AddHandler(PointerMovedEvent, OnLinkedListPointerMoved, RoutingStrategies.Tunnel);
    }

    // --- Drag-and-drop reorder ---

    private void OnLinkedListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(LinkedHardwareList);
        _draggedItem = LinkedHardwareList.SelectedItem as JobHardware;
        _isDragging = false;
    }

    private void OnLinkedListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedItem == null) return;
        var pos = e.GetPosition(LinkedHardwareList);
        var delta = pos - _dragStartPoint;
        if (!_isDragging && (Math.Abs(delta.Y) > 8))
            _isDragging = true;
    }

    private void OnLinkedListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging || _draggedItem == null) { _isDragging = false; _draggedItem = null; return; }

        var releasePos = e.GetPosition(LinkedHardwareList);
        var targetItem = GetItemAtPosition(releasePos);

        if (targetItem != null && !ReferenceEquals(targetItem, _draggedItem))
        {
            var fromIndex = _linkedHardware.IndexOf(_draggedItem);
            var toIndex = _linkedHardware.IndexOf(targetItem);
            if (fromIndex >= 0 && toIndex >= 0)
                _linkedHardware.Move(fromIndex, toIndex);
        }

        _isDragging = false;
        _draggedItem = null;
    }

    private JobHardware? GetItemAtPosition(Point position)
    {
        if (_linkedHardware.Count == 0) return null;
        var itemHeight = LinkedHardwareList.Bounds.Height / _linkedHardware.Count;
        if (itemHeight <= 0) return null;
        var index = (int)(position.Y / itemHeight);
        index = Math.Clamp(index, 0, _linkedHardware.Count - 1);
        return _linkedHardware[index];
    }

    // --- Search ---

    /// <summary>
    /// Repopulates the Description search combo to show only descriptions that have at least
    /// one hardware item made by the selected manufacturer, then re-runs the search.
    /// </summary>
    private void OnSearchMfrChanged()
    {
        var mfr = SearchMfrCombo.SelectedItem as Manufacturer;
        var mfrId = mfr?.Id ?? 0;

        var filtered = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };

        if (mfrId == 0)
        {
            filtered.AddRange(_descComboItems);
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = ctx.HardwareItems
                .Where(h => h.ManufacturerId == mfrId)
                .Select(h => h.DescriptionId)
                .Distinct()
                .ToHashSet();
            filtered.AddRange(_descComboItems.Where(d => descIds.Contains(d.Id)));
        }

        _updatingSearchDescCombo = true;
        try
        {
            SearchDescCombo.ItemsSource = filtered;
            SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
            SearchDescCombo.SelectedIndex = 0;
        }
        finally
        {
            _updatingSearchDescCombo = false;
        }

        SearchHardware();
    }

    // --- Hardware management ---

    private void LoadLinkedHardware()
    {
        _linkedHardware.Clear();
        using var context = DatabaseInitializer.CreateContext();
        var query = context.JobHardware
            .Include(jh => jh.HardwareItem)
                .ThenInclude(h => h.Manufacturer)
            .Include(jh => jh.HardwareItem)
                .ThenInclude(h => h.Description)
            .Where(jh => jh.JobId == _jobId);

        if (_currentReleaseId == null)
            query = query.Where(jh => jh.ReleaseId == null);
        else
            query = query.Where(jh => jh.ReleaseId == _currentReleaseId);

        foreach (var row in query.ToList())
            _linkedHardware.Add(row);
    }

    private void SearchHardware()
    {
        var mfr      = SearchMfrCombo.SelectedItem as Manufacturer;
        var descItem = SearchDescCombo.SelectedItem as DescriptionComboItem;
        var model    = SearchModelBox.Text?.Trim();

        var mfrName = (mfr      == null || mfr.Id      == 0) ? null : mfr.ManufacturerName;
        var descId  = (descItem == null || descItem.Id == 0) ? (int?)null : descItem.Id;

        using var context = DatabaseInitializer.CreateContext();
        var repo = new HardwareItemRepository(context);
        var results = repo.Search(mfrName, descId, model).ToList();

        HardwareSearchList.ItemsSource = results;
        HardwareSearchList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
    }

    private void AddHardwareToJob()
    {
        if (HardwareSearchList.SelectedItem is not HardwareItem h) { LinkStatusLabel.Text = "Select a hardware item to add."; return; }

        var customDesc  = CustomDescBox.Text?.Trim();
        var customLabel = string.IsNullOrWhiteSpace(customDesc) ? null : customDesc;

        using var context = DatabaseInitializer.CreateContext();

        // Duplicate check: same item + same custom label (including both null) is a duplicate.
        bool isDuplicate = context.JobHardware.Any(jh =>
            jh.JobId             == _jobId &&
            jh.HardwareItemId    == h.Id   &&
            jh.CustomDescription == customLabel);

        if (isDuplicate)
        {
            LinkStatusLabel.Text = $"Already in job: {h.ModelNumber}"
                + (customLabel != null ? $" ({customLabel})" : string.Empty);
            return;
        }

        var freqService = new FrequencyService(context);
        var hardwareItem = context.HardwareItems.Find(h.Id);
        if (hardwareItem != null) freqService.IncrementFrequency(hardwareItem);

        _jobHardwareRepo!.Add(new JobHardware
        {
            JobId             = _jobId,
            HardwareItemId    = h.Id,
            CustomDescription = customLabel,
            ReleaseId         = _currentReleaseId
        });

        CustomDescBox.Text   = "";
        LinkStatusLabel.Text = $"Added: {h.ModelNumber}";
        LoadLinkedHardware();
    }

    private void RemoveHardwareFromJob()
    {
        if (LinkedHardwareList.SelectedItem is not JobHardware jh) { LinkStatusLabel.Text = "Select a linked item to remove."; return; }

        using var context = DatabaseInitializer.CreateContext();
        var link = context.JobHardware.Find(jh.Id);
        if (link != null)
        {
            context.JobHardware.Remove(link);
            context.SaveChanges();
            LinkStatusLabel.Text = $"Removed: {jh.DisplayLabel}";
            LoadLinkedHardware();
        }
    }

    // --- Phase 20: Job complete status ---

    /// <summary>Updates the Mark Complete / Reactivate button visibility and status label.</summary>
    private void UpdateCompleteButtons()
    {
        MarkCompleteButton.IsVisible = !_isComplete;
        ReactivateButton.IsVisible   = _isComplete;
        JobStatusLabel.Text          = _isComplete ? "[COMPLETE]" : string.Empty;
    }

    private async Task MarkCompleteAsync()
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        bool markDone = await Helpers.DialogHelper.ConfirmAsync(win,
            "Mark this job as complete?\n\nCompleted jobs are hidden from the main list but remain searchable.",
            "Mark Job Complete");
        if (!markDone) return;

        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_jobId);
        if (job == null) return;
        job.IsComplete = true;
        ctx.SaveChanges();

        _isComplete = true;
        UpdateCompleteButtons();
    }

    private void Reactivate()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_jobId);
        if (job == null) return;
        job.IsComplete = false;
        ctx.SaveChanges();

        _isComplete = false;
        UpdateCompleteButtons();
    }

    // --- Phase 21 is wired in Initialize via ToggleLinkedHardwareButton ---

    // --- Phase 22: Sort + Edit hardware ---

    /// <summary>Sorts the linked hardware list in-place by manufacturer or description name.</summary>
    private void SortLinkedHardware(string by)
    {
        var sorted = by == "mfr"
            ? _linkedHardware.OrderBy(jh => jh.HardwareItem?.Manufacturer?.ManufacturerName ?? string.Empty)
                             .ThenBy(jh => jh.HardwareItem?.ModelNumber ?? string.Empty)
                             .ToList()
            : _linkedHardware.OrderBy(jh => jh.HardwareItem?.Description?.DescriptionText ?? string.Empty)
                             .ThenBy(jh => jh.HardwareItem?.ModelNumber ?? string.Empty)
                             .ToList();

        _linkedHardware.Clear();
        foreach (var item in sorted)
            _linkedHardware.Add(item);
    }

    /// <summary>Saves the edited custom label for the currently selected hardware link.</summary>
    private void SaveHardwareEdit()
    {
        if (LinkedHardwareList.SelectedItem is not JobHardware jh)
        {
            EditHardwareStatusLabel.Text = "No item selected.";
            return;
        }

        var newLabel = EditCustomDescBox.Text?.Trim();
        newLabel = string.IsNullOrEmpty(newLabel) ? null : newLabel;

        using var ctx = DatabaseInitializer.CreateContext();
        var link = ctx.JobHardware.Find(jh.Id);
        if (link == null) { EditHardwareStatusLabel.Text = "Record not found."; return; }

        link.CustomDescription = newLabel;
        ctx.SaveChanges();

        EditHardwareStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        EditHardwareStatusLabel.Text = "Saved.";
        EditHardwarePanel.IsVisible = false;
        LoadLinkedHardware();
    }

    // --- Phase 23: Job releases ---

    /// <summary>
    /// Populates the ReleaseCombo with "(Base list)" + all named releases for this job.
    /// </summary>
    private void LoadReleaseCombo()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var releases = ctx.JobReleases
            .Where(r => r.JobId == _jobId)
            .OrderBy(r => r.ReleaseNumber)
            .ToList();

        var items = new System.Collections.Generic.List<object>
        {
            new { Label = "(Base list)", ReleaseId = (int?)null }
        };
        foreach (var r in releases)
            items.Add(new { Label = r.DisplayLabel, ReleaseId = (int?)r.Id });

        ReleaseCombo.ItemsSource = items;
        ReleaseCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Label");
        ReleaseCombo.SelectedIndex = 0;
    }

    private void OnReleaseChanged()
    {
        if (ReleaseCombo.SelectedItem == null) return;
        // Use reflection-style dynamic to avoid introducing a named type.
        var item = ReleaseCombo.SelectedItem;
        var prop = item.GetType().GetProperty("ReleaseId");
        _currentReleaseId = prop?.GetValue(item) as int?;
        LoadLinkedHardware();
    }

    private void CreateRelease()
    {
        var label = ReleaseLabelBox.Text?.Trim();
        if (string.IsNullOrEmpty(label))
        {
            ReleaseStatusLabel.Text = "Enter a release label.";
            return;
        }

        using var ctx = DatabaseInitializer.CreateContext();
        var repo   = new JobReleaseRepository(ctx);
        var number = repo.NextReleaseNumber(_jobId);
        var release = repo.Add(new HardwareTemplateBuilder.Core.Models.JobRelease
        {
            JobId         = _jobId,
            ReleaseNumber = number,
            ReleaseLabel  = label,
            Notes         = string.IsNullOrWhiteSpace(ReleaseNotesBox.Text) ? null : ReleaseNotesBox.Text.Trim()
        });

        ReleaseLabelBox.Text  = string.Empty;
        ReleaseNotesBox.Text  = string.Empty;
        NewReleasePanel.IsVisible = false;
        ReleaseStatusLabel.Text   = string.Empty;

        // Refresh combo and select the new release.
        LoadReleaseCombo();
        // Select the last item (the new release).
        ReleaseCombo.SelectedIndex = ReleaseCombo.ItemCount - 1;
    }

    // --- Generate Package ---

    /// <summary>
    /// Checks that every template linked to the job's hardware items has at least one
    /// usable file source: a non-empty <c>LocalLink</c> whose file exists on disk,
    /// or a non-empty <c>OnlineLink</c>.
    /// </summary>
    private string? RunPreflightCheck()
    {
        using var context = DatabaseInitializer.CreateContext();

        var hardwareIds = context.JobHardware
            .Where(jh => jh.JobId == _jobId)
            .Select(jh => jh.HardwareItemId)
            .ToList();

        var problems = new List<string>();

        foreach (var hwId in hardwareIds)
        {
            var templates = context.HardwareItemTemplates
                .Include(hit => hit.IndividualTemplate)
                    .ThenInclude(t => t.Manufacturer)
                .Where(hit => hit.HardwareItemId == hwId)
                .Select(hit => hit.IndividualTemplate)
                .ToList();

            foreach (var t in templates)
            {
                bool hasOnline    = !string.IsNullOrWhiteSpace(t.OnlineLink);
                bool hasLocalFile = !string.IsNullOrWhiteSpace(t.LocalLink) && File.Exists(t.LocalLink);

                if (!hasOnline && !hasLocalFile)
                {
                    var mfr    = t.Manufacturer?.ManufacturerName ?? "Unknown";
                    var reason = string.IsNullOrWhiteSpace(t.LocalLink)
                        ? "no local or online link"
                        : "local file not found and no online link";
                    problems.Add($"  \u2022 {mfr} {t.TemplateNumber}: {reason}");
                }
            }
        }

        if (problems.Count == 0) return null;

        return "The following templates cannot be acquired:\n\n"
             + string.Join("\n", problems)
             + "\n\nSet a Local Link or Online Link for each template, then retry.";
    }

    private async Task OnGeneratePackageAsync()
    {
        if (_linkedHardware.Count == 0)
        {
            PackageStatusLabel.Text = "Add hardware items to the job first.";
            return;
        }

        var preflightMessage = RunPreflightCheck();
        if (preflightMessage != null)
        {
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
                await DialogHelper.ShowInfoAsync(win, preflightMessage, "Cannot Generate Package");
            else
                PackageStatusLabel.Text = "Some templates are missing file links. Fix them before generating.";
            return;
        }

        _packageCts?.Cancel();
        _packageCts = new CancellationTokenSource();
        var ct = _packageCts.Token;

        GeneratePackageButton.IsEnabled = false;
        PackageProgress.IsVisible = true;
        PackageStatusLabel.Text = "Loading job data...";

        var orderedJobHardware = _linkedHardware.Select(jh => (jh.HardwareItemId, jh.CustomDescription)).ToList();

        string outputPath;
        IReadOnlyList<TemplateSnapshotInfo> snapshots;

        try
        {
            (outputPath, snapshots) = await Task.Run(async () =>
            {
                using var context = DatabaseInitializer.CreateContext();

                var job = context.Jobs
                    .Include(j => j.Customer)
                    .Include(j => j.ProjectManager)
                    .First(j => j.Id == _jobId);

                var allItemIds = orderedJobHardware.Select(x => x.HardwareItemId).Distinct().ToList();
                var hardwareDict = context.HardwareItems
                    .Include(h => h.Manufacturer)
                    .Include(h => h.Description)
                    .Where(h => allItemIds.Contains(h.Id))
                    .ToDictionary(h => h.Id);

                var hardware = orderedJobHardware
                    .Where(x => hardwareDict.ContainsKey(x.HardwareItemId))
                    .Select(x =>
                    {
                        var templates = context.HardwareItemTemplates
                            .Include(hit => hit.IndividualTemplate)
                                .ThenInclude(t => t.Manufacturer)
                            .Include(hit => hit.IndividualTemplate)
                                .ThenInclude(t => t.Description)
                            .Where(hit => hit.HardwareItemId == x.HardwareItemId)
                            .Select(hit => hit.IndividualTemplate)
                            .ToList()
                            .AsReadOnly();

                        return new HardwareWithTemplates
                        {
                            Item              = hardwareDict[x.HardwareItemId],
                            Templates         = templates,
                            CustomDescription = x.CustomDescription
                        };
                    })
                    .ToList()
                    .AsReadOnly();

                var profile = context.UserProfiles
                    .FirstOrDefault(u => u.Id == job.UserProfileId);
                var saveDir = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
                    ? profile.DefaultTemplateSaveLocation
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                var allDescriptions = context.Descriptions
                    .ToDictionary(d => d.Id);

                var request = new AssemblyRequest
                {
                    Job             = job,
                    Hardware        = hardware,
                    OutputDirectory = saveDir,
                    AllDescriptions = allDescriptions,
                    PreparedByName  = profile?.UserName ?? string.Empty
                };

                // Back up the entire job folder contents before regenerating.
                // Everything except the "Old versions" folder is moved into
                // Old versions/{timestamp}/ so the previous output is fully preserved.
                var jobDir = Path.Combine(saveDir, job.JobNumber);
                if (Directory.Exists(jobDir))
                {
                    var entries = Directory.GetFileSystemEntries(jobDir)
                        .Where(e => !Path.GetFileName(e).Equals("Old versions",
                                        StringComparison.OrdinalIgnoreCase)
                                 && !Path.GetFileName(e).Equals("Attachments",
                                        StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (entries.Count > 0)
                    {
                        var versionStamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
                        var backupDir    = Path.Combine(jobDir, "Old versions", versionStamp);
                        Directory.CreateDirectory(backupDir);

                        foreach (var entry in entries)
                        {
                            var dest = Path.Combine(backupDir, Path.GetFileName(entry));
                            if (File.Exists(entry))
                                File.Move(entry, dest);
                            else if (Directory.Exists(entry))
                                Directory.Move(entry, dest);
                        }

                        Dispatcher.UIThread.Post(() => PackageStatusLabel.Text = "Backed up previous version...");
                    }
                }

                var progress = new Progress<string>(msg =>
                    Dispatcher.UIThread.Post(() => PackageStatusLabel.Text = msg));

                var service = BuildAssemblyService();
                var result  = await service.AssembleAsync(request, progress, ct);

                // Append a history entry to job_history.txt in the job folder.
                var historyPath = Path.Combine(saveDir, job.JobNumber, "job_history.txt");
                AppendHistoryEntry(historyPath, job, hardware, result, profile?.UserName ?? "Unknown");

                return (result.OutputPath, result.TemplateSnapshots);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            PackageStatusLabel.Text = "Cancelled.";
            return;
        }
        catch (Exception ex)
        {
            PackageStatusLabel.Text = $"Error: {ex.Message}";
            return;
        }
        finally
        {
            GeneratePackageButton.IsEnabled = true;
            PackageProgress.IsVisible = false;
        }

        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var snapshotRepo = new JobTemplateSnapshotRepository(ctx);
            foreach (var info in snapshots)
            {
                snapshotRepo.Add(new JobTemplateSnapshot
                {
                    JobId                = _jobId,
                    IndividualTemplateId = info.IndividualTemplateId,
                    SnapshotLocalLink    = info.AcquiredFilePath,
                    SnapshotDate         = DateTime.UtcNow,
                    PagesToPrint         = info.PagesToPrint,
                    PagesToRotate        = info.PagesToRotate,
                    RotationDirection    = info.RotationDirection
                });
            }

            var freqService = new FrequencyService(ctx);
            foreach (var itemId in orderedJobHardware.Select(x => x.HardwareItemId).Distinct())
            {
                var item = ctx.HardwareItems.Find(itemId);
                if (item != null) freqService.IncrementFrequency(item);
            }
        }

        PackageStatusLabel.Text = $"Generated: {Path.GetFileName(outputPath)}";

        try
        {
            Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal: file was generated but couldn't be auto-opened.
        }

        // Phase 20: ask whether to mark job complete (only if not already complete).
        if (!_isComplete)
        {
            var winForComplete = TopLevel.GetTopLevel(this) as Window;
            if (winForComplete != null)
            {
                bool markDone = await Helpers.DialogHelper.ConfirmAsync(winForComplete,
                    "Package generated successfully.\n\nIs this job done? Mark it as complete?",
                    "Mark Job Complete?");
                if (markDone)
                {
                    using var ctx2 = DatabaseInitializer.CreateContext();
                    var job2 = ctx2.Jobs.Find(_jobId);
                    if (job2 != null) { job2.IsComplete = true; ctx2.SaveChanges(); }
                    _isComplete = true;
                    UpdateCompleteButtons();
                }
            }
        }
    }

    /// <summary>
    /// Opens the "Old versions" folder for this job in the OS file explorer, if it exists.
    /// </summary>
    private void BrowseOldVersions()
    {
        string saveDir;
        string jobNumber;
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var job     = ctx.Jobs.Find(_jobId);
            jobNumber   = job?.JobNumber ?? string.Empty;
            var profile = ctx.UserProfiles.FirstOrDefault(u => u.Id == (job != null ? job.UserProfileId : 0));
            saveDir     = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
                ? profile.DefaultTemplateSaveLocation
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var oldVersionsDir = Path.Combine(saveDir, jobNumber, "Old versions");
        if (!Directory.Exists(oldVersionsDir))
        {
            PackageStatusLabel.Text = "No old versions folder found for this job.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(oldVersionsDir) { UseShellExecute = true });
        }
        catch
        {
            PackageStatusLabel.Text = $"Could not open folder: {oldVersionsDir}";
        }
    }

    // --- Attachments ---

    private void LoadAttachments()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var attachments = new JobAttachmentRepository(ctx).GetByJob(_jobId);
        AttachmentList.ItemsSource = attachments;
    }

    /// <summary>
    /// Resolves the job's Attachments subfolder path, creating it if needed.
    /// Returns null when the job or its save location cannot be determined.
    /// </summary>
    private string? GetAttachmentsFolder()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var job     = ctx.Jobs.Find(_jobId);
        if (job == null) return null;
        var profile = ctx.UserProfiles.FirstOrDefault(u => u.Id == job.UserProfileId);
        var saveDir = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
            ? profile.DefaultTemplateSaveLocation
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = Path.Combine(saveDir, job.JobNumber, "Attachments");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task AddAttachmentAsync(string fileType)
    {
        AttachmentStatusLabel.Text = "";
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;

        var filters = fileType == "Email"
            ? new[] { new FilePickerFileType("Email / PDF") { Patterns = new[] { "*.eml", "*.msg", "*.pdf" } } }
            : new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = $"Select {fileType} file",
            AllowMultiple = true,
            FileTypeFilter = filters
        });

        if (files.Count == 0) return;

        var folder = GetAttachmentsFolder();
        if (folder == null) { AttachmentStatusLabel.Text = "Could not resolve job folder."; return; }

        var notes = AttachmentNoteBox.Text?.Trim();
        int added = 0;

        using var ctx = DatabaseInitializer.CreateContext();
        var repo = new JobAttachmentRepository(ctx);

        foreach (var file in files)
        {
            var sourcePath = file.Path.LocalPath;
            var fileName   = Path.GetFileName(sourcePath);
            var destPath   = Path.Combine(folder, fileName);

            // If a file with that name already exists, append a counter.
            if (File.Exists(destPath))
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext  = Path.GetExtension(fileName);
                int n = 1;
                while (File.Exists(destPath))
                    destPath = Path.Combine(folder, $"{stem} ({n++}){ext}");
            }

            File.Copy(sourcePath, destPath);

            repo.Add(new JobAttachment
            {
                JobId      = _jobId,
                FileName   = Path.GetFileName(destPath),
                StoredPath = destPath,
                FileType   = fileType,
                Notes      = string.IsNullOrWhiteSpace(notes) ? null : notes,
                DateAdded  = DateTime.UtcNow
            });
            added++;
        }

        AttachmentNoteBox.Text = "";
        AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        AttachmentStatusLabel.Text = $"Added {added} file{(added == 1 ? "" : "s")}.";
        LoadAttachments();
    }

    private void OpenAttachment()
    {
        if (AttachmentList.SelectedItem is not JobAttachment a)
        {
            AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            AttachmentStatusLabel.Text = "Select an attachment to open.";
            return;
        }
        if (!File.Exists(a.StoredPath))
        {
            AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            AttachmentStatusLabel.Text = "File not found on disk.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(a.StoredPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            AttachmentStatusLabel.Text = $"Could not open: {ex.Message}";
        }
    }

    private void RemoveAttachment()
    {
        if (AttachmentList.SelectedItem is not JobAttachment a)
        {
            AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            AttachmentStatusLabel.Text = "Select an attachment to remove.";
            return;
        }

        using var ctx = DatabaseInitializer.CreateContext();
        var record = ctx.JobAttachments.Find(a.Id);
        if (record != null)
        {
            ctx.JobAttachments.Remove(record);
            ctx.SaveChanges();
        }

        // Delete the copied file if it still exists.
        try { if (File.Exists(a.StoredPath)) File.Delete(a.StoredPath); } catch { }

        AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        AttachmentStatusLabel.Text = $"Removed: {a.FileName}";
        LoadAttachments();
    }

    // --- Quick Create ---

    private bool _newHardwarePanelReady;
    private bool _newDescriptionPanelReady;
    private bool _newTemplatePanelReady;

    private void TogglePanel(Border panel, Action init)
    {
        if (!panel.IsVisible)
        {
            init();
            panel.IsVisible = true;
        }
        else
        {
            panel.IsVisible = false;
        }
    }

    private void InitNewHardwarePanel()
    {
        if (_newHardwarePanelReady) return;
        _newHardwarePanelReady = true;

        using var ctx = DatabaseInitializer.CreateContext();
        var mfrs  = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        var descs = DescriptionHelper.BuildComboItems(new DescriptionRepository(ctx).GetAll());

        NewHwMfrCombo.ItemsSource = mfrs;
        NewHwMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        NewHwDescCombo.ItemsSource = descs;
        NewHwDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
    }

    private void InitNewDescriptionPanel()
    {
        if (_newDescriptionPanelReady) return;
        _newDescriptionPanelReady = true;
        RefreshNewDescParentCombo();
    }

    private void RefreshNewDescParentCombo()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var items = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(None — top level)" } };
        items.AddRange(DescriptionHelper.BuildComboItems(new DescriptionRepository(ctx).GetAll()));
        NewDescParentCombo.ItemsSource = items;
        NewDescParentCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        NewDescParentCombo.SelectedIndex = 0;
    }

    private void InitNewTemplatePanel()
    {
        if (_newTemplatePanelReady) return;
        _newTemplatePanelReady = true;

        using var ctx = DatabaseInitializer.CreateContext();
        var descs      = DescriptionHelper.BuildComboItems(new DescriptionRepository(ctx).GetAll());
        var materials  = ctx.DoorMaterials.OrderBy(m => m.Material).ToList();

        NewTplDescCombo.ItemsSource = descs;
        NewTplDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        NewTplMaterialCombo.ItemsSource = materials;
        NewTplMaterialCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");
        if (materials.Count > 0) NewTplMaterialCombo.SelectedIndex = 0;

        RefreshNewTemplateHardwareCombo();
    }

    private void RefreshNewTemplateHardwareCombo()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var items = ctx.JobHardware
            .Include(jh => jh.HardwareItem).ThenInclude(h => h.Manufacturer)
            .Where(jh => jh.JobId == _jobId)
            .ToList();
        NewTplHardwareCombo.ItemsSource = items;
        NewTplHardwareCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");
        if (items.Count > 0) NewTplHardwareCombo.SelectedIndex = 0;
    }

    private void CreateHardwareItem()
    {
        NewHwStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
        NewHwStatusLabel.Text = string.Empty;

        var mfr  = NewHwMfrCombo.SelectedItem  as Manufacturer;
        var desc = NewHwDescCombo.SelectedItem as DescriptionComboItem;
        var model = NewHwModelBox.Text?.Trim();

        if (mfr == null)   { NewHwStatusLabel.Text = "Select a manufacturer."; return; }
        if (desc == null)  { NewHwStatusLabel.Text = "Select a description."; return; }
        if (string.IsNullOrEmpty(model)) { NewHwStatusLabel.Text = "Enter a model number."; return; }

        using var ctx = DatabaseInitializer.CreateContext();
        var repo = new HardwareItemRepository(ctx);
        var item = repo.Add(new HardwareItem
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            ModelNumber    = model,
            Remarks        = string.IsNullOrWhiteSpace(NewHwNotesBox.Text) ? null : NewHwNotesBox.Text.Trim()
        });

        if (NewHwAddToJobCheck.IsChecked == true)
        {
            new JobHardwareRepository(ctx).Add(new JobHardware
            {
                JobId          = _jobId,
                HardwareItemId = item.Id
            });
            LoadLinkedHardware();
            RefreshNewTemplateHardwareCombo();
        }

        NewHwStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        NewHwStatusLabel.Text = $"Created: {model}" + (NewHwAddToJobCheck.IsChecked == true ? " (added to job)" : string.Empty);
        NewHwModelBox.Text  = string.Empty;
        NewHwNotesBox.Text  = string.Empty;
    }

    private void CreateDescription()
    {
        NewDescStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
        NewDescStatusLabel.Text = string.Empty;

        var name = NewDescNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { NewDescStatusLabel.Text = "Enter a description name."; return; }

        var parent = NewDescParentCombo.SelectedItem as DescriptionComboItem;
        int? parentId = (parent == null || parent.Id == 0) ? null : parent.Id;

        using var ctx = DatabaseInitializer.CreateContext();
        new DescriptionRepository(ctx).Add(new Description
        {
            DescriptionText = name,
            ParentId        = parentId
        });

        // Refresh all description combos so the new entry is immediately available.
        _descComboItems = DescriptionHelper.BuildComboItems(new DescriptionRepository(ctx).GetAll());

        var anyDesc = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        anyDesc.AddRange(_descComboItems);
        SearchDescCombo.ItemsSource = anyDesc;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        if (_newHardwarePanelReady)
        {
            NewHwDescCombo.ItemsSource = _descComboItems;
            NewHwDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        }
        if (_newTemplatePanelReady)
        {
            NewTplDescCombo.ItemsSource = _descComboItems;
            NewTplDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        }

        RefreshNewDescParentCombo();

        NewDescStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        NewDescStatusLabel.Text = $"Created: {name}";
        NewDescNameBox.Text = string.Empty;
    }

    private async Task CreateTemplateAsync()
    {
        NewTplStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
        NewTplStatusLabel.Text = string.Empty;

        var jobHw   = NewTplHardwareCombo.SelectedItem as JobHardware;
        var desc    = NewTplDescCombo.SelectedItem     as DescriptionComboItem;
        var material = NewTplMaterialCombo.SelectedItem as DoorMaterial;
        var number  = NewTplNumberBox.Text?.Trim();
        var pages   = NewTplPagesBox.Text?.Trim();
        var online  = NewTplOnlineLinkBox.Text?.Trim();
        var local   = NewTplLocalLinkBox.Text?.Trim();

        if (jobHw == null)  { NewTplStatusLabel.Text = "Select a hardware item to link to."; return; }
        if (desc == null)   { NewTplStatusLabel.Text = "Select a description."; return; }
        if (material == null) { NewTplStatusLabel.Text = "Select a door material."; return; }
        if (string.IsNullOrEmpty(number)) { NewTplStatusLabel.Text = "Enter a template number."; return; }
        if (string.IsNullOrEmpty(pages))  { NewTplStatusLabel.Text = "Enter pages to print."; return; }
        if (string.IsNullOrEmpty(online) && string.IsNullOrEmpty(local))
        { NewTplStatusLabel.Text = "Provide at least an online link or a local file path."; return; }

        using var ctx = DatabaseInitializer.CreateContext();

        var hw = ctx.HardwareItems.Find(jobHw.HardwareItemId);
        if (hw == null) { NewTplStatusLabel.Text = "Hardware item not found."; return; }

        var template = new IndividualTemplate
        {
            ManufacturerId   = hw.ManufacturerId,
            DescriptionId    = desc.Id,
            TemplateNumber   = number,
            PagesToPrint     = pages,
            DoorMaterialId   = material.Id,
            OnlineLink       = string.IsNullOrEmpty(online) ? null : online,
            LocalLink        = string.IsNullOrEmpty(local)  ? null : local
        };

        ctx.IndividualTemplates.Add(template);
        ctx.SaveChanges();

        ctx.HardwareItemTemplates.Add(new HardwareItemTemplate
        {
            HardwareItemId       = hw.Id,
            IndividualTemplateId = template.Id
        });
        ctx.SaveChanges();

        NewTplStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        NewTplStatusLabel.Text = $"Created and linked: {number}";
        NewTplNumberBox.Text = string.Empty;
        NewTplPagesBox.Text  = string.Empty;
        NewTplOnlineLinkBox.Text = string.Empty;
        NewTplLocalLinkBox.Text  = string.Empty;
    }

    private async Task BrowseLocalFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select Template PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });

        if (files.Count > 0)
            NewTplLocalLinkBox.Text = files[0].Path.LocalPath;
    }

    // --- History ---

    /// <summary>
    /// Appends a single generation record to <paramref name="historyPath"/>, creating the file
    /// if it does not yet exist.  Each record captures the timestamp, user, hardware list,
    /// and output file name so the full history of a job's packages is preserved in plain text.
    /// </summary>
    private static void AppendHistoryEntry(
        string historyPath,
        Job job,
        IReadOnlyList<HardwareWithTemplates> hardware,
        AssemblyResult result,
        string preparedBy)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Job:         {job.JobNumber} — {job.JobName}");
            sb.AppendLine($"Prepared by: {preparedBy}");
            sb.AppendLine($"Hardware Items ({hardware.Count}):");

            int n = 1;
            foreach (var hwt in hardware)
            {
                var item  = hwt.Item;
                var mfr   = item.Manufacturer?.ManufacturerName ?? "Unknown";
                var desc  = item.Description?.DescriptionText   ?? string.Empty;
                var label = !string.IsNullOrWhiteSpace(hwt.CustomDescription)
                    ? hwt.CustomDescription!
                    : item.ModelNumber;
                var tplNums = string.Join(", ", hwt.Templates.Select(t => t.TemplateNumber));

                sb.AppendLine($"  {n++,2}. {mfr} — {label}" +
                              (string.IsNullOrEmpty(desc) ? string.Empty : $" [{desc}]"));
                if (!string.IsNullOrEmpty(tplNums))
                    sb.AppendLine($"       Templates: {tplNums}");
            }

            sb.AppendLine($"Output: {Path.GetFileName(result.OutputPath)}");
            sb.AppendLine(new string('─', 60));
            sb.AppendLine();

            File.AppendAllText(historyPath, sb.ToString());
        }
        catch
        {
            // History write failure is non-fatal.
        }
    }

    /// <summary>Wires a toggle button to show/hide a panel and update the ▼/▲ arrow.</summary>
    private static void WireToggle(Button button, Avalonia.Controls.Control body)
    {
        button.Click += (_, _) =>
        {
            body.IsVisible     = !body.IsVisible;
            button.Content     = body.IsVisible ? "▲" : "▼";
        };
    }

    /// <summary>Creates a fully wired <see cref="PdfAssemblyService"/> with all required dependencies.</summary>
    private static PdfAssemblyService BuildAssemblyService()
    {
        // Bypass SSL certificate errors when downloading manufacturer PDFs.
        // Manufacturer sites frequently have expired or chain-incomplete certificates;
        // the URLs are user-supplied and trusted, so validation is not meaningful here.
        var handler = new System.Net.Http.HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new PdfAssemblyService(
            new TemplateSorter(new WeightTemplateSortStrategy()),
            new FileAcquirer(new HttpClient(handler)),
            new PageRangeParser(),
            new PageExtractor(),
            new PageRotator(),
            new PdfMerger(),
            new CoverSheetBuilder(),
            new PageNumberer());
    }
}
