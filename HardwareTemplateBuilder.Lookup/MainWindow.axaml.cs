using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Core.Services.Pdf;
using HardwareTemplateBuilder.Lookup.Helpers;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Lookup;

/// <summary>
/// Standalone template lookup window. Lists hardware items filtered by manufacturer,
/// description, and model number. Selecting an item and clicking Open Templates
/// generates and opens a merged PDF of all linked templates.
/// Read-only: never writes to the database.
/// </summary>
public partial class MainWindow : Window
{
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _leafDescComboItems = new();
    private CancellationTokenSource? _cts;
    private bool _dbAvailable;

    /// <summary>Initializes the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    // ---------- Initialization ----------

    private void Initialize()
    {
        try
        {
            InitializeCore();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Startup error: {ex.Message}";
        }
    }

    private void InitializeCore()
    {
        var dbPath = DatabaseInitializer.GetDatabasePath();
        if (!File.Exists(dbPath))
        {
            StatusLabel.Text =
                $"Database not found at: {dbPath}\nLaunch Hardware Template Builder first to create it.";
            return;
        }

        DatabaseInitializer.Initialize();

        _dbAvailable = true;

        using var ctx = DatabaseInitializer.CreateContext();

        var allDescriptions = new DescriptionRepository(ctx).GetAll();

        _manufacturers = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();

        // Leaf-only — intermediate parent categories aren't meaningful hardware-item
        // descriptions on their own.
        _leafDescComboItems = DescriptionHelper.BuildLeafComboItems(allDescriptions);

        MfrList.ItemsSource = _manufacturers;
        MfrList.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        MfrList.SelectionChanged += (_, _) => { RefreshDescList(); RefreshModelList(); };
        DescList.SelectionChanged += (_, _) => RefreshModelList();
        ModelList.SelectionChanged += (_, _) => OnModelSelected();
        OpenButton.Click += async (_, _) => await OnOpenClickedAsync();

        // Show every description/model on first load; selections narrow both from there.
        RefreshDescList();
        RefreshModelList();
    }

    // ---------- Search ----------

    /// <summary>
    /// Repopulates the Description list to show only leaf descriptions that have at least one
    /// active hardware item under any of the currently selected manufacturers (all leaf
    /// descriptions if none are selected).
    /// </summary>
    private void RefreshDescList()
    {
        if (!_dbAvailable) return;

        var mfrIds = SelectedIds(MfrList, (Manufacturer m) => m.Id);

        List<DescriptionComboItem> items;
        if (mfrIds.Count == 0)
        {
            items = _leafDescComboItems;
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = ctx.HardwareItems
                .Where(h => h.IsActive && mfrIds.Contains(h.ManufacturerId))
                .Select(h => h.DescriptionId)
                .Distinct()
                .ToHashSet();
            items = _leafDescComboItems.Where(d => descIds.Contains(d.Id)).ToList();
        }

        DescList.ItemsSource = items;
        DescList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
    }

    /// <summary>
    /// Repopulates the Model # list with every hardware item matching the currently selected
    /// Manufacturers and Descriptions — a match is any item whose manufacturer is among the
    /// selected manufacturers (if any are selected) AND whose description is among the selected
    /// descriptions (if any are selected). With nothing selected in either list, every item is
    /// shown, narrowing only as selections are made.
    /// </summary>
    private void RefreshModelList()
    {
        if (!_dbAvailable) return;

        var mfrIds  = SelectedIds(MfrList, (Manufacturer m) => m.Id);
        var descIds = SelectedIds(DescList, (DescriptionComboItem d) => d.Id);

        using var ctx = DatabaseInitializer.CreateContext();
        var query = ctx.HardwareItems
            .Include(h => h.Manufacturer)
            .Include(h => h.Description)
            .Where(h => h.IsActive)
            .AsQueryable();

        if (mfrIds.Count > 0) query = query.Where(h => mfrIds.Contains(h.ManufacturerId));
        if (descIds.Count > 0) query = query.Where(h => descIds.Contains(h.DescriptionId));

        var results = query
            .OrderByDescending(h => h.Frequency)
            .ThenBy(h => h.ModelNumber)
            .ToList();

        ModelList.ItemsSource = results;
        ModelList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");

        if (results.Count > 0)
            ModelList.SelectedIndex = 0;
        else
            OnModelSelected();
    }

    private void OnModelSelected()
    {
        if (ModelList.SelectedItem is HardwareItem item)
        {
            SelectedMfrLabel.Text   = $"Manufacturer: {item.Manufacturer?.ManufacturerName}";
            SelectedDescLabel.Text  = $"Type: {item.Description?.DescriptionText}";
            SelectedModelLabel.Text = $"Model: {item.ModelNumber}";
            SelectedFreqLabel.Text  = $"Frequency: {item.Frequency}";
            OpenButton.IsEnabled = true;
        }
        else
        {
            SelectedMfrLabel.Text   = string.Empty;
            SelectedDescLabel.Text  = string.Empty;
            SelectedModelLabel.Text = string.Empty;
            SelectedFreqLabel.Text  = string.Empty;
            OpenButton.IsEnabled = false;
        }
    }

    /// <summary>Returns the IDs of the currently selected items in a multi-select ListBox.</summary>
    private static HashSet<int> SelectedIds<T>(ListBox listBox, Func<T, int> idSelector) =>
        listBox.SelectedItems?.Cast<T>().Select(idSelector).ToHashSet() ?? new HashSet<int>();

    // ---------- Generate / Open PDF ----------

    private async Task OnOpenClickedAsync()
    {
        if (ModelList.SelectedItem is not HardwareItem item)
        {
            StatusLabel.Text = "Please select a hardware item first.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        OpenButton.IsEnabled = false;
        GenerateProgress.IsVisible = true;
        StatusLabel.Text = "Starting...";

        string outputPath;
        try
        {
            outputPath = await GeneratePdfAsync(item, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Cancelled.";
            return;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            return;
        }
        finally
        {
            OpenButton.IsEnabled = true;
            GenerateProgress.IsVisible = false;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(outputPath) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal: file generated but couldn't be auto-opened.
        }

        StatusLabel.Text = $"Opened: {Path.GetFileName(outputPath)}";
    }

    /// <summary>
    /// Loads all linked templates for <paramref name="item"/>, sorts them, acquires/processes
    /// each PDF, and merges them into a single file saved to the configured output location.
    /// Runs heavy work on the thread pool; posts status updates to the UI thread.
    /// </summary>
    private async Task<string> GeneratePdfAsync(HardwareItem item, CancellationToken ct)
    {
        List<IndividualTemplate> templates;
        string saveLocation;
        Dictionary<int, Description> allDescriptions;

        using (var ctx = DatabaseInitializer.CreateContext())
        {
            templates = ctx.HardwareItemTemplates
                .Include(hit => hit.IndividualTemplate)
                    .ThenInclude(t => t.Manufacturer)
                .Include(hit => hit.IndividualTemplate)
                    .ThenInclude(t => t.Description)
                .Where(hit => hit.HardwareItemId == item.Id)
                .Select(hit => hit.IndividualTemplate)
                .ToList();

            var configured = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
            saveLocation = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.GetTempPath();

            allDescriptions = ctx.Descriptions.ToDictionary(d => d.Id);
        }

        if (templates.Count == 0)
            throw new InvalidOperationException(
                "This hardware item has no linked templates.");

        var sortedTemplates = new TemplateSorter(new WeightTemplateSortStrategy())
            .Sort(templates, allDescriptions);

        return await Task.Run(async () =>
        {
            var workDir = Path.Combine(Path.GetTempPath(), $"htb_lookup_{item.Id}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);

            var socketsHandler = new System.Net.Http.SocketsHttpHandler();
            socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            var acquirer  = new FileAcquirer(new HttpClient(socketsHandler));
            var parser    = new PageRangeParser();
            var extractor = new PageExtractor();
            var rotator   = new PageRotator();
            var merger    = new PdfMerger();
            var processed = new List<string>();

            foreach (var template in sortedTemplates)
            {
                ct.ThrowIfCancellationRequested();
                PostStatus($"Acquiring {template.TemplateNumber}...");
                var acquired = await acquirer.AcquireAsync(template, workDir);

                PostStatus($"Processing {template.TemplateNumber}...");
                var pageNumbers = parser.Parse(template.PagesToPrint);
                var extracted = Path.Combine(workDir, $"ex_{template.Id}.pdf");
                extractor.Extract(acquired, pageNumbers, extracted);

                string processedPath = extracted;
                if (!string.IsNullOrWhiteSpace(template.PagesToRotate))
                {
                    var rotateOrig = parser.Parse(template.PagesToRotate);
                    var indexMap   = pageNumbers
                        .Select((p, i) => (Orig: p, Idx: i + 1))
                        .ToDictionary(x => x.Orig, x => x.Idx);
                    var rotateIdx  = rotateOrig
                        .Where(p => indexMap.ContainsKey(p))
                        .Select(p => indexMap[p])
                        .ToList();
                    if (rotateIdx.Count > 0)
                    {
                        processedPath = Path.Combine(workDir, $"rot_{template.Id}.pdf");
                        rotator.Rotate(extracted, rotateIdx, template.RotationDirection, processedPath);
                    }
                }

                processed.Add(processedPath);
            }

            ct.ThrowIfCancellationRequested();
            PostStatus("Merging PDFs...");
            var merged = Path.Combine(workDir, "merged.pdf");
            merger.Merge(processed, merged);

            Directory.CreateDirectory(saveLocation);
            var outputPath = Path.Combine(saveLocation, $"{SanitizeFileName(item.ModelNumber)}_templates.pdf");
            File.Copy(merged, outputPath, overwrite: true);
            return outputPath;
        }, ct);
    }

    // ---------- Helpers ----------

    private void PostStatus(string message) =>
        Dispatcher.UIThread.Post(() => StatusLabel.Text = message);

    private static string SanitizeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
