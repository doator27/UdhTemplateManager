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
/// CRUD view for <see cref="Job"/> records, including a linked-hardware sub-panel
/// for managing <see cref="JobHardware"/> junction records with drag-and-drop reordering.
/// </summary>
public partial class JobsView : UserControl
{
    private JobRepository? _repo;
    private JobHardwareRepository? _jobHardwareRepo;
    private List<Customer> _customers = new();
    private List<ProjectManager> _projectManagers = new();
    private List<UserProfile> _userProfiles = new();
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _descriptions = new();

    /// <summary>The observable collection backing the linked hardware listbox, enabling drag-and-drop reorder.</summary>
    private ObservableCollection<HardwareItem> _linkedHardware = new();

    private int _selectedJobId;

    /// <summary>Cancellation source for any in-progress package generation.</summary>
    private CancellationTokenSource? _packageCts;

    /// <summary>The hardware item being dragged for reorder operations.</summary>
    private HardwareItem? _draggedItem;

    private bool _isDragging;
    private global::Avalonia.Point _dragStartPoint;

    /// <summary>Initializes the view.</summary>
    public JobsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new JobRepository(context);
        _jobHardwareRepo = new JobHardwareRepository(context);

        _customers = new CustomerRepository(context).GetAll().OrderBy(c => c.CustomerName).ToList();
        _projectManagers = new ProjectManagerRepository(context).GetAll().OrderBy(pm => pm.ProjectManagerName).ToList();
        _userProfiles = new UserProfileRepository(context).GetAll().OrderBy(u => u.UserName).ToList();
        _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descriptions = new DescriptionRepository(context).GetAll().OrderBy(d => d.DescriptionText).ToList();

        CustomerCombo.ItemsSource = _customers;
        CustomerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("CustomerName");
        ProjectManagerCombo.ItemsSource = _projectManagers;
        ProjectManagerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ProjectManagerName");
        UserProfileCombo.ItemsSource = _userProfiles;
        UserProfileCombo.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");

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
        LinkedHardwareList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");

        LoadJobList();

        FilterBox.TextChanged += (_, _) => LoadJobList();
        JobList.SelectionChanged += (_, _) => OnJobSelected();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this job?"))
                DeleteSelected();
        };

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
        _draggedItem = LinkedHardwareList.SelectedItem as HardwareItem;
        _isDragging = false;
    }

    private void OnLinkedListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedItem == null) return;
        var pos = e.GetPosition(LinkedHardwareList);
        var delta = pos - _dragStartPoint;
        if (!_isDragging && (System.Math.Abs(delta.Y) > 8))
            _isDragging = true;
    }

    private void OnLinkedListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging || _draggedItem == null) { _isDragging = false; _draggedItem = null; return; }

        // Determine drop target by hit-testing the release position
        var releasePos = e.GetPosition(LinkedHardwareList);
        var targetItem = GetItemAtPosition(releasePos);

        if (targetItem != null && !ReferenceEquals(targetItem, _draggedItem))
        {
            var fromIndex = _linkedHardware.IndexOf(_draggedItem);
            var toIndex = _linkedHardware.IndexOf(targetItem);
            if (fromIndex >= 0 && toIndex >= 0)
            {
                _linkedHardware.Move(fromIndex, toIndex);
            }
        }

        _isDragging = false;
        _draggedItem = null;
    }

    private HardwareItem? GetItemAtPosition(global::Avalonia.Point position)
    {
        // Walk the items and estimate position based on item height
        if (_linkedHardware.Count == 0) return null;
        var itemHeight = LinkedHardwareList.Bounds.Height / _linkedHardware.Count;
        if (itemHeight <= 0) return null;
        var index = (int)(position.Y / itemHeight);
        index = System.Math.Clamp(index, 0, _linkedHardware.Count - 1);
        return _linkedHardware[index];
    }

    // --- Job list ---

    private void LoadJobList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(j => string.IsNullOrEmpty(filter) ||
                        j.JobNumber.ToLower().Contains(filter) ||
                        j.JobName.ToLower().Contains(filter))
            .OrderBy(j => j.JobNumber)
            .ToList();
        JobList.ItemsSource = items;
        JobList.DisplayMemberBinding = new Avalonia.Data.Binding("JobNumber");
    }

    private void OnJobSelected()
    {
        if (JobList.SelectedItem is Job j)
        {
            _selectedJobId = j.Id;
            JobNumberBox.Text = j.JobNumber;
            JobNameBox.Text = j.JobName;
            CustomerCombo.SelectedItem = _customers.FirstOrDefault(c => c.Id == j.CustomerId);
            ProjectManagerCombo.SelectedItem = _projectManagers.FirstOrDefault(pm => pm.Id == j.ProjectManagerId);
            UserProfileCombo.SelectedItem = _userProfiles.FirstOrDefault(u => u.Id == j.UserProfileId);
            StatusLabel.Text = "";
            LoadLinkedHardware();
        }
    }

    private void LoadLinkedHardware()
    {
        _linkedHardware.Clear();
        if (_selectedJobId == 0) return;

        using var context = DatabaseInitializer.CreateContext();
        var items = context.JobHardware
            .Include(jh => jh.HardwareItem)
            .ThenInclude(h => h.Manufacturer)
            .Where(jh => jh.JobId == _selectedJobId)
            .Select(jh => jh.HardwareItem)
            .ToList();

        foreach (var item in items)
            _linkedHardware.Add(item);
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
        if (_selectedJobId == 0) { LinkStatusLabel.Text = "Select a job first."; return; }
        if (HardwareSearchList.SelectedItem is not HardwareItem h) { LinkStatusLabel.Text = "Select a hardware item to add."; return; }

        // Increment frequency on add
        using var context = DatabaseInitializer.CreateContext();
        var freqService = new FrequencyService(context);
        var hardwareItem = context.HardwareItems.Find(h.Id);
        if (hardwareItem != null) freqService.IncrementFrequency(hardwareItem);

        _jobHardwareRepo!.Add(new JobHardware { JobId = _selectedJobId, HardwareItemId = h.Id });
        LinkStatusLabel.Text = $"Added: {h.ModelNumber}";
        LoadLinkedHardware();
    }

    private void RemoveHardwareFromJob()
    {
        if (_selectedJobId == 0) { LinkStatusLabel.Text = "Select a job first."; return; }
        if (LinkedHardwareList.SelectedItem is not HardwareItem h) { LinkStatusLabel.Text = "Select a linked item to remove."; return; }

        using var context = DatabaseInitializer.CreateContext();
        var link = context.JobHardware.FirstOrDefault(jh => jh.JobId == _selectedJobId && jh.HardwareItemId == h.Id);
        if (link != null)
        {
            context.JobHardware.Remove(link);
            context.SaveChanges();
            LinkStatusLabel.Text = $"Removed: {h.ModelNumber}";
            LoadLinkedHardware();
        }
    }

    // --- Generate Package ---

    /// <summary>
    /// Checks that every template linked to the job's hardware items has at least one
    /// usable file source: a non-empty <c>LocalLink</c> whose file exists on disk,
    /// or a non-empty <c>OnlineLink</c>.
    /// </summary>
    /// <returns>
    /// Null if all templates pass; otherwise a user-facing message listing the failures.
    /// </returns>
    private string? RunPreflightCheck(int jobId)
    {
        using var context = DatabaseInitializer.CreateContext();

        var hardwareIds = context.JobHardware
            .Where(jh => jh.JobId == jobId)
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
    /// Returns the expected final output path for the job package without creating any files,
    /// or null if the job or its user profile cannot be resolved.
    /// </summary>
    private string? GetExpectedOutputPath(int jobId)
    {
        using var context = DatabaseInitializer.CreateContext();
        var job = context.Jobs.Find(jobId);
        if (job == null) return null;

        var profile = context.UserProfiles.FirstOrDefault(u => u.Id == job.UserProfileId);
        var saveDir = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
            ? profile.DefaultTemplateSaveLocation
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(saveDir, job.JobNumber, $"{job.JobNumber}_templates.pdf");
    }

    private async Task OnGeneratePackageAsync()
    {
        if (_selectedJobId == 0)
        {
            PackageStatusLabel.Text = "Select a job first.";
            return;
        }
        if (_linkedHardware.Count == 0)
        {
            PackageStatusLabel.Text = "Add hardware items to the job first.";
            return;
        }

        // Pre-flight: ensure every linked template has a usable file source.
        var preflightMessage = RunPreflightCheck(_selectedJobId);
        if (preflightMessage != null)
        {
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
                await DialogHelper.ShowInfoAsync(win, preflightMessage, "Cannot Generate Package");
            else
                PackageStatusLabel.Text = "Some templates are missing file links. Fix them before generating.";
            return;
        }

        // Overwrite check: warn if a package file already exists.
        var expectedPath = GetExpectedOutputPath(_selectedJobId);
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

        // Capture state from UI thread before switching to background.
        int jobId = _selectedJobId;
        var orderedHardwareIds = _linkedHardware.Select(h => h.Id).ToList();

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
                    .First(j => j.Id == jobId);

                var hardwareDict = context.HardwareItems
                    .Include(h => h.Manufacturer)
                    .Include(h => h.Description)
                    .Where(h => orderedHardwareIds.Contains(h.Id))
                    .ToDictionary(h => h.Id);

                // Preserve drag-ordered sequence and load templates per item.
                var hardware = orderedHardwareIds
                    .Where(id => hardwareDict.ContainsKey(id))
                    .Select(id =>
                    {
                        var templates = context.HardwareItemTemplates
                            .Include(hit => hit.IndividualTemplate)
                                .ThenInclude(t => t.Manufacturer)
                            .Include(hit => hit.IndividualTemplate)
                                .ThenInclude(t => t.Weight)
                            .Where(hit => hit.HardwareItemId == id)
                            .Select(hit => hit.IndividualTemplate)
                            .ToList()
                            .AsReadOnly();

                        return new HardwareWithTemplates
                        {
                            Item      = hardwareDict[id],
                            Templates = templates
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

        // Write immutable JobTemplateSnapshot records and increment frequency.
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var snapshotRepo = new JobTemplateSnapshotRepository(ctx);
            foreach (var info in snapshots)
            {
                snapshotRepo.Add(new JobTemplateSnapshot
                {
                    JobId                = jobId,
                    IndividualTemplateId = info.IndividualTemplateId,
                    SnapshotLocalLink    = info.AcquiredFilePath,
                    SnapshotDate         = DateTime.UtcNow,
                    PagesToPrint         = info.PagesToPrint,
                    PagesToRotate        = info.PagesToRotate,
                    RotationDirection    = info.RotationDirection
                });
            }

            var freqService = new FrequencyService(ctx);
            foreach (var itemId in orderedHardwareIds)
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

    /// <summary>
    /// Creates a fully wired <see cref="PdfAssemblyService"/> with all required dependencies.
    /// </summary>
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

    // --- Save / Delete / Clear ---

    private void Save()
    {
        var jobNumber = JobNumberBox.Text?.Trim();
        var jobName = JobNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(jobNumber)) { StatusLabel.Text = "Job Number is required."; return; }
        if (string.IsNullOrEmpty(jobName)) { StatusLabel.Text = "Job Name is required."; return; }
        if (CustomerCombo.SelectedItem is not Customer customer) { StatusLabel.Text = "Customer is required."; return; }
        if (ProjectManagerCombo.SelectedItem is not ProjectManager pm) { StatusLabel.Text = "Project Manager is required."; return; }
        if (UserProfileCombo.SelectedItem is not UserProfile user) { StatusLabel.Text = "User Profile is required."; return; }

        if (_selectedJobId == 0)
        {
            _repo!.Add(new Job
            {
                JobNumber = jobNumber,
                JobName = jobName,
                CustomerId = customer.Id,
                ProjectManagerId = pm.Id,
                UserProfileId = user.Id
            });
        }
        else
        {
            var existing = _repo!.GetById(_selectedJobId);
            if (existing != null)
            {
                existing.JobNumber = jobNumber;
                existing.JobName = jobName;
                existing.CustomerId = customer.Id;
                existing.ProjectManagerId = pm.Id;
                existing.UserProfileId = user.Id;
                _repo.Update(existing);
            }
        }
        StatusLabel.Text = "Saved.";
        LoadJobList();
    }

    private void DeleteSelected()
    {
        if (_selectedJobId == 0) return;
        _repo!.Delete(_selectedJobId);
        ClearForm();
        LoadJobList();
    }

    private void ClearForm()
    {
        _selectedJobId = 0;
        JobNumberBox.Text = "";
        JobNameBox.Text = "";
        CustomerCombo.SelectedItem = null;
        ProjectManagerCombo.SelectedItem = null;
        UserProfileCombo.SelectedItem = null;
        StatusLabel.Text = "";
        _linkedHardware.Clear();
        JobList.SelectedItem = null;
    }
}
