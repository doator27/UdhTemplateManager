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

        LinkedHardwareList.ItemsSource = _linkedHardware;
        LinkedHardwareList.DisplayMemberBinding = new Avalonia.Data.Binding("FullDisplayLabel");

        LoadLinkedHardware();

        // Attachments
        AttachmentList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");
        LoadAttachments();
        AddEmailButton.Click          += async (_, _) => await AddAttachmentAsync("Email");
        AddPdfButton.Click            += async (_, _) => await AddAttachmentAsync("PDF");
        OpenAttachmentButton.Click    += (_, _) => OpenAttachment();
        RemoveAttachmentButton.Click  += (_, _) => RemoveAttachment();

        BackButton.Click += (_, _) => NavigationRequested?.Invoke("Jobs");
        BulkAddButton.Click += (_, _) => NavigationRequested?.Invoke($"BulkManufacturerSelection:{_jobId}");
        ImportHardwareButton.Click += async (_, _) => await ImportHardwareAsync();

        // Phase 20: Mark complete / Reactivate
        MarkCompleteButton.Click  += async (_, _) => await MarkCompleteAsync();
        ReactivateButton.Click    += (_, _) => Reactivate();

        RemoveHardwareButton.Click                += (_, _) => RemoveHardwareFromJob();
        GeneratePackageButton.Click               += async (_, _) => await OnGeneratePackageAsync();
        GenerateForManufacturerButton.Click       += async (_, _) => await OnGenerateCoverSheetForManufacturerAsync();
        OpenCurrentPackageButton.Click     += (_, _) => OpenCurrentPackage();
        BrowseOldVersionsButton.Click      += (_, _) => BrowseOldVersions();

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

    // --- Hardware management ---

    private void LoadLinkedHardware()
    {
        _linkedHardware.Clear();
        using var context = DatabaseInitializer.CreateContext();
        var rows = context.JobHardware
            .Include(jh => jh.HardwareItem)
                .ThenInclude(h => h.Manufacturer)
            .Include(jh => jh.HardwareItem)
                .ThenInclude(h => h.Description)
            .Where(jh => jh.JobId == _jobId && jh.ReleaseId == null)
            .OrderBy(jh => jh.HardwareItem!.Manufacturer!.ManufacturerName)
            .ThenBy(jh => jh.HardwareItem!.Description!.DescriptionText)
            .ThenBy(jh => jh.HardwareItem!.ModelNumber)
            .ToList();

        foreach (var row in rows)
            _linkedHardware.Add(row);

        LoadManufacturersForJob();
    }

    private void LoadManufacturersForJob()
    {
        var names = _linkedHardware
            .Select(jh => jh.HardwareItem?.Manufacturer?.ManufacturerName ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = ManufacturerFilterComboBox.SelectedItem as string;
        ManufacturerFilterComboBox.ItemsSource = names;
        if (previous != null && names.Contains(previous))
            ManufacturerFilterComboBox.SelectedItem = previous;
        else if (names.Count > 0)
            ManufacturerFilterComboBox.SelectedIndex = 0;
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

    // --- Import hardware ---

    /// <summary>
    /// Shows the import picker dialog and, if a source job is chosen, loads its hardware
    /// into the template resolution wizard for review.
    /// </summary>
    private async Task ImportHardwareAsync()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var dialog = new ImportJobPickerDialog(_jobId);
        var sourceJob = await dialog.ShowDialog<Job?>(window);
        if (sourceJob == null) return;

        using var ctx = DatabaseInitializer.CreateContext();

        var sourceRows = ctx.JobHardware
            .Include(jh => jh.HardwareItem).ThenInclude(h => h.Manufacturer)
            .Include(jh => jh.HardwareItem).ThenInclude(h => h.Description)
            .Where(jh => jh.JobId == sourceJob.Id && jh.ReleaseId == null)
            .OrderBy(jh => jh.Id)
            .AsNoTracking()
            .ToList();

        if (sourceRows.Count == 0)
        {
            await DialogHelper.ShowInfoAsync(window, "The selected job has no hardware to import.", "Nothing to Import");
            return;
        }

        // Group labels by hardware item, preserving the first-appearance order.
        var orderedIds = new List<int>();
        var groups     = new Dictionary<int, List<JobHardware>>();
        foreach (var row in sourceRows)
        {
            if (!groups.ContainsKey(row.HardwareItemId))
            {
                orderedIds.Add(row.HardwareItemId);
                groups[row.HardwareItemId] = new List<JobHardware>();
            }
            groups[row.HardwareItemId].Add(row);
        }

        var pendingRows = new List<BulkHardwareRow>();
        foreach (var hwId in orderedIds)
        {
            var labelRows = groups[hwId];
            var hw        = labelRows[0].HardwareItem;

            var jobScopedTemplates = ctx.HardwareItemTemplates
                .Include(hit => hit.IndividualTemplate)
                .Where(hit => hit.HardwareItemId == hwId && hit.JobId == sourceJob.Id)
                .AsNoTracking()
                .ToList();

            pendingRows.Add(new BulkHardwareRow
            {
                MatchedItem          = hw,
                SelectedManufacturer = hw.Manufacturer,
                ModelNumber          = hw.ModelNumber,
                HardwareItemRemarks  = hw.Remarks,
                Labels               = labelRows.Select(r => new BulkHardwareLabel
                {
                    CustomLabel = r.CustomDescription ?? "",
                    Remarks     = r.Remarks
                }).ToList(),
                PendingTemplates = jobScopedTemplates.Select(hit => new BulkPendingTemplate
                {
                    Template      = hit.IndividualTemplate,
                    IsJobSpecific = true
                }).ToList()
            });
        }

        BulkAddSession.PendingRows = pendingRows;
        BulkAddSession.JobId       = _jobId;
        NavigationRequested?.Invoke($"TemplateResolutionWizard:{_jobId}");
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
                .Where(hit => hit.HardwareItemId == hwId &&
                              (hit.JobId == null || hit.JobId == _jobId))
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

        var orderedJobHardware = _linkedHardware.Select(jh => 
            (jh.HardwareItemId, 
             jh.CustomDescription, 
             // For backwards compatibility: prefer CalloutRemarks, fallback to Remarks
             CalloutRemarks: !string.IsNullOrWhiteSpace(jh.CalloutRemarks) ? jh.CalloutRemarks : jh.Remarks))
            .ToList();

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
                            .Where(hit => hit.HardwareItemId == x.HardwareItemId &&
                                          (hit.JobId == null || hit.JobId == _jobId))
                            .Select(hit => hit.IndividualTemplate)
                            .ToList()
                            .AsReadOnly();

                        return new HardwareWithTemplates
                        {
                            Item              = hardwareDict[x.HardwareItemId],
                            Templates         = templates,
                            CustomDescription = x.CustomDescription,
                            CalloutRemarks    = x.CalloutRemarks
                        };
                    })
                    .ToList()
                    .AsReadOnly();

                var profile = context.UserProfiles
                    .FirstOrDefault(u => u.Id == job.UserProfileId);
                var saveDir = new AppSettingRepository(context).GetValue("TemplateStorageLocation");
                if (string.IsNullOrWhiteSpace(saveDir))
                    saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

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

                // Create a single shared HTTP client (SSL bypass for manufacturer sites).
                using var httpHandler = new System.Net.Http.HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                using var httpClient = new HttpClient(httpHandler);

                // Pre-download any templates that don't have a valid local file yet.
                // TemplateRefreshService uses the same context, so EF's identity map means
                // the LocalLink update lands on the same template objects already in `hardware`.
                var allUniqueTemplates = hardware
                    .SelectMany(h => h.Templates)
                    .GroupBy(t => t.Id)
                    .Select(g => g.First())
                    .Where(t => string.IsNullOrWhiteSpace(t.LocalLink) || !File.Exists(t.LocalLink))
                    .ToList();

                if (allUniqueTemplates.Count > 0)
                {
                    var refreshService = new TemplateRefreshService(context, httpClient);
                    for (int i = 0; i < allUniqueTemplates.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var t = allUniqueTemplates[i];
                        Dispatcher.UIThread.Post(() => PackageStatusLabel.Text =
                            $"Downloading {t.TemplateNumber} ({i + 1}/{allUniqueTemplates.Count})...");
                        try
                        {
                            await refreshService.RefreshSingleAsync(t.Id, saveDir, ct);
                        }
                        catch
                        {
                            // Download failures are reported by AssembleAsync's per-template error collection.
                        }
                    }
                }

                var service = BuildAssemblyService(httpClient);
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
            PackageStatusLabel.Text = "Generation failed — see error report.";
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
                await DialogHelper.ShowScrollableInfoAsync(win, ex.Message, "Package Generation Failed");
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
    /// Opens the most recently modified PDF in the job's output folder (excluding the
    /// "Old versions" and "Attachments" subdirectories).
    /// </summary>
    private void OpenCurrentPackage()
    {
        string saveDir;
        string jobNumber;
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var job   = ctx.Jobs.Find(_jobId);
            jobNumber = job?.JobNumber ?? string.Empty;
            saveDir   = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
            if (string.IsNullOrWhiteSpace(saveDir))
                saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var jobDir = Path.Combine(saveDir, jobNumber);
        if (!Directory.Exists(jobDir))
        {
            PackageStatusLabel.Text = "No output folder found for this job.";
            return;
        }

        var pdf = Directory.GetFiles(jobDir, "*.pdf")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();

        if (pdf == null)
        {
            PackageStatusLabel.Text = "No package found for this job.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true });
        }
        catch
        {
            PackageStatusLabel.Text = $"Could not open: {Path.GetFileName(pdf)}";
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
            saveDir = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
            if (string.IsNullOrWhiteSpace(saveDir))
                saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
        var saveDir = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
        if (string.IsNullOrWhiteSpace(saveDir))
            saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = Path.Combine(saveDir, job.JobNumber, "Attachments");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task AddAttachmentAsync(string fileType)
    {
        try
        {
            AttachmentStatusLabel.Text = "";
            AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            
            var topLevel = TopLevel.GetTopLevel(this) as Window;
            if (topLevel == null)
            {
                AttachmentStatusLabel.Text = "Could not determine parent window.";
                return;
            }

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
            if (folder == null)
            {
                AttachmentStatusLabel.Text = "Could not resolve job folder.";
                return;
            }

            var notes = AttachmentNoteBox.Text?.Trim();
            int added = 0;

            using var ctx = DatabaseInitializer.CreateContext();
            var repo = new JobAttachmentRepository(ctx);

            foreach (var file in files)
            {
                if (file?.Path == null)
                {
                    AttachmentStatusLabel.Text = "Invalid file selection.";
                    continue;
                }

                var sourcePath = file.Path.LocalPath;
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    AttachmentStatusLabel.Text = $"File not found: {sourcePath}";
                    continue;
                }

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
        catch (Exception ex)
        {
            AttachmentStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            AttachmentStatusLabel.Text = $"Error adding attachment: {ex.Message}";
        }
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

    // --- Per-manufacturer cover sheet ---

    /// <summary>
    /// Generates a standalone cover sheet PDF containing only the hardware items belonging to the
    /// manufacturer selected in <see cref="ManufacturerFilterComboBox"/>. No template PDFs are
    /// merged; page numbers are left blank.
    /// </summary>
    private async Task OnGenerateCoverSheetForManufacturerAsync()
    {
        if (ManufacturerFilterComboBox.SelectedItem is not string manufacturerName)
        {
            PackageStatusLabel.Text = "Select a manufacturer first.";
            return;
        }

        var filteredHardware = _linkedHardware
            .Where(jh => jh.HardwareItem?.Manufacturer?.ManufacturerName == manufacturerName)
            .ToList();

        if (filteredHardware.Count == 0)
        {
            PackageStatusLabel.Text = "No hardware items found for the selected manufacturer.";
            return;
        }

        GenerateForManufacturerButton.IsEnabled = false;
        PackageProgress.IsVisible = true;
        PackageStatusLabel.Text = "Building cover sheet...";

        string outputPath;
        try
        {
            outputPath = await Task.Run(() =>
            {
                using var context = DatabaseInitializer.CreateContext();

                var job = context.Jobs
                    .Include(j => j.Customer)
                    .Include(j => j.ProjectManager)
                    .First(j => j.Id == _jobId);

                var profile = context.UserProfiles.FirstOrDefault(u => u.Id == job.UserProfileId);
                var saveDir = new AppSettingRepository(context).GetValue("TemplateStorageLocation");
                if (string.IsNullOrWhiteSpace(saveDir))
                    saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                var allItemIds = filteredHardware.Select(jh => jh.HardwareItemId).Distinct().ToList();
                var hardwareDict = context.HardwareItems
                    .Include(h => h.Manufacturer)
                    .Include(h => h.Description)
                    .Where(h => allItemIds.Contains(h.Id))
                    .ToDictionary(h => h.Id);

                var rows = new List<CoverSheetRow>();
                foreach (var jh in filteredHardware)
                {
                    if (!hardwareDict.TryGetValue(jh.HardwareItemId, out var item)) continue;

                    var templateNumbers = context.HardwareItemTemplates
                        .Include(hit => hit.IndividualTemplate)
                        .Where(hit => hit.HardwareItemId == jh.HardwareItemId &&
                                      (hit.JobId == null || hit.JobId == _jobId))
                        .Select(hit => hit.IndividualTemplate.TemplateNumber)
                        .ToList();

                    var calloutRemark = !string.IsNullOrWhiteSpace(jh.CalloutRemarks) ? jh.CalloutRemarks : jh.Remarks;
                    string? combinedRemarks = (item.Remarks, calloutRemark) switch
                    {
                        (null or "", null or "") => null,
                        (var a, null or "")      => a,
                        (null or "", var b)      => b,
                        (var a, var b)           => $"{a} | {b}"
                    };

                    rows.Add(new CoverSheetRow
                    {
                        Manufacturer        = item.Manufacturer?.ManufacturerName ?? string.Empty,
                        HardwareType        = item.Description?.DescriptionText   ?? string.Empty,
                        HardwareDescription = !string.IsNullOrWhiteSpace(jh.CustomDescription)
                                                ? jh.CustomDescription!
                                                : item.ModelNumber,
                        TemplateNumbers     = string.Join(", ", templateNumbers),
                        PageNumbers         = string.Empty,
                        Remarks             = combinedRemarks
                    });
                }

                var coverData = new CoverSheetData
                {
                    JobNumber          = job.JobNumber,
                    JobName            = job.JobName,
                    CustomerName       = job.Customer?.CustomerName             ?? string.Empty,
                    ProjectManagerName = job.ProjectManager?.ProjectManagerName ?? string.Empty,
                    DateCreated        = DateTime.Now,
                    PreparedBy         = profile?.UserName ?? string.Empty,
                    Rows               = rows.AsReadOnly()
                };

                var safeName = string.Concat(manufacturerName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
                var jobDir = Path.Combine(saveDir, job.JobNumber);
                Directory.CreateDirectory(jobDir);
                var outPath = Path.Combine(jobDir, $"Cover Sheet - {safeName}.pdf");

                new CoverSheetBuilder().Build(coverData, outPath);
                return outPath;
            });
        }
        catch (Exception ex)
        {
            PackageStatusLabel.Text = "Cover sheet generation failed.";
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
                await DialogHelper.ShowScrollableInfoAsync(win, ex.Message, "Cover Sheet Generation Failed");
            return;
        }
        finally
        {
            GenerateForManufacturerButton.IsEnabled = true;
            PackageProgress.IsVisible = false;
        }

        PackageStatusLabel.Text = $"Generated: {Path.GetFileName(outputPath)}";
        try { Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Creates a fully wired <see cref="PdfAssemblyService"/> using the supplied HTTP client.</summary>
    private static PdfAssemblyService BuildAssemblyService(HttpClient httpClient)
    {
        return new PdfAssemblyService(
            new TemplateSorter(new WeightTemplateSortStrategy()),
            new FileAcquirer(httpClient),
            new PageRangeParser(),
            new PageExtractor(),
            new PageRotator(),
            new PdfMerger(),
            new CoverSheetBuilder(),
            new PageNumberer());
    }
}
