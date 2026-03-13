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
    private List<Description> _descriptions = new();

    /// <summary>The observable collection backing the linked hardware listbox, enabling drag-and-drop reorder.</summary>
    private readonly ObservableCollection<JobHardware> _linkedHardware = new();

    /// <summary>Cancellation source for any in-progress package generation.</summary>
    private CancellationTokenSource? _packageCts;

    /// <summary>The hardware row being dragged for reorder operations.</summary>
    private JobHardware? _draggedItem;

    private bool _isDragging;
    private Point _dragStartPoint;

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
        _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descriptions = new DescriptionRepository(context).GetAll().OrderBy(d => d.DescriptionText).ToList();

        // Show job header
        var job = context.Jobs.Find(_jobId);
        JobTitleLabel.Text = job != null ? $"Job: {job.JobNumber} — {job.JobName}" : $"Job #{_jobId}";

        // Search combos
        var anyMfr = new List<Manufacturer> { new Manufacturer { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        SearchMfrCombo.ItemsSource = anyMfr;
        SearchMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        SearchMfrCombo.SelectedIndex = 0;

        var anyDesc = new List<Description> { new Description { Id = 0, DescriptionText = "(Any)" } };
        anyDesc.AddRange(_descriptions);
        SearchDescCombo.ItemsSource = anyDesc;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
        SearchDescCombo.SelectedIndex = 0;

        LinkedHardwareList.ItemsSource = _linkedHardware;
        LinkedHardwareList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");

        LoadLinkedHardware();

        BackButton.Click += (_, _) => NavigationRequested?.Invoke("Jobs");
        SearchMfrCombo.SelectionChanged += (_, _) => SearchHardware();
        SearchDescCombo.SelectionChanged += (_, _) => SearchHardware();
        SearchModelBox.TextChanged += (_, _) => SearchHardware();
        AddHardwareButton.Click += (_, _) => AddHardwareToJob();
        RemoveHardwareButton.Click += (_, _) => RemoveHardwareFromJob();
        GeneratePackageButton.Click += async (_, _) => await OnGeneratePackageAsync();

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
            .Where(jh => jh.JobId == _jobId)
            .ToList();
        foreach (var row in rows)
            _linkedHardware.Add(row);
    }

    private void SearchHardware()
    {
        var mfr = SearchMfrCombo.SelectedItem as Manufacturer;
        var desc = SearchDescCombo.SelectedItem as Description;
        var model = SearchModelBox.Text?.Trim();

        var mfrName = (mfr == null || mfr.Id == 0) ? null : mfr.ManufacturerName;
        var descText = (desc == null || desc.Id == 0) ? null : desc.DescriptionText;

        using var context = DatabaseInitializer.CreateContext();
        var repo = new HardwareItemRepository(context);
        var results = repo.Search(mfrName, descText, model).ToList();

        HardwareSearchList.ItemsSource = results;
        HardwareSearchList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
    }

    private void AddHardwareToJob()
    {
        if (HardwareSearchList.SelectedItem is not HardwareItem h) { LinkStatusLabel.Text = "Select a hardware item to add."; return; }

        var customDesc = CustomDescBox.Text?.Trim();

        using var context = DatabaseInitializer.CreateContext();
        var freqService = new FrequencyService(context);
        var hardwareItem = context.HardwareItems.Find(h.Id);
        if (hardwareItem != null) freqService.IncrementFrequency(hardwareItem);

        _jobHardwareRepo!.Add(new JobHardware
        {
            JobId             = _jobId,
            HardwareItemId    = h.Id,
            CustomDescription = string.IsNullOrWhiteSpace(customDesc) ? null : customDesc
        });

        CustomDescBox.Text = "";
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

    /// <summary>
    /// Returns the expected final output path for the job package without creating any files.
    /// </summary>
    private string? GetExpectedOutputPath()
    {
        using var context = DatabaseInitializer.CreateContext();
        var job = context.Jobs.Find(_jobId);
        if (job == null) return null;

        var profile = context.UserProfiles.FirstOrDefault(u => u.Id == job.UserProfileId);
        var saveDir = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
            ? profile.DefaultTemplateSaveLocation
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(saveDir, job.JobNumber, $"{job.JobNumber}_templates.pdf");
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

        var expectedPath = GetExpectedOutputPath();
        if (expectedPath != null && File.Exists(expectedPath))
        {
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
            {
                var overwrite = await DialogHelper.ConfirmAsync(win,
                    $"A package already exists:\n{Path.GetFileName(expectedPath)}\n\nOverwrite it?",
                    "File Already Exists");
                if (!overwrite) return;
            }
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

                var request = new AssemblyRequest
                {
                    Job             = job,
                    Hardware        = hardware,
                    OutputDirectory = saveDir
                };

                var progress = new Progress<string>(msg =>
                    Dispatcher.UIThread.Post(() => PackageStatusLabel.Text = msg));

                var service = BuildAssemblyService();
                var result  = await service.AssembleAsync(request, progress, ct);
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
    }

    /// <summary>Creates a fully wired <see cref="PdfAssemblyService"/> with all required dependencies.</summary>
    private static PdfAssemblyService BuildAssemblyService() =>
        new PdfAssemblyService(
            new TemplateSorter(new WeightTemplateSortStrategy(new WeightParser())),
            new FileAcquirer(new HttpClient()),
            new PageRangeParser(),
            new PageExtractor(),
            new PageRotator(),
            new PdfMerger(),
            new CoverSheetBuilder(),
            new PageNumberer());
}
