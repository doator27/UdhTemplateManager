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
    private List<DescriptionComboItem> _descComboItems = new();
    private CancellationTokenSource? _cts;
    private bool _updatingDescCombo;
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

        _manufacturers  = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(new DescriptionRepository(ctx).GetAll());

        var anyMfr = new List<Manufacturer> { new() { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        MfrCombo.ItemsSource = anyMfr;
        MfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        MfrCombo.SelectedIndex = 0;

        var anyDesc = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        anyDesc.AddRange(_descComboItems);
        DescCombo.ItemsSource = anyDesc;
        DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        DescCombo.SelectedIndex = 0;

        MfrCombo.SelectionChanged  += (_, _) => OnMfrChanged();
        DescCombo.SelectionChanged += (_, _) => { if (!_updatingDescCombo) SearchHardware(); };
        ModelBox.TextChanged       += (_, _) => SearchHardware();

        ResultsList.SelectionChanged += (_, _) => OnResultSelected();
        OpenButton.Click += async (_, _) => await OnOpenClickedAsync();

        SearchHardware();
    }

    // ---------- Search ----------

    /// <summary>
    /// Repopulates the Description combo to show only descriptions that have at least one
    /// active hardware item made by the selected manufacturer, then re-runs the search.
    /// </summary>
    private void OnMfrChanged()
    {
        var mfr   = MfrCombo.SelectedItem as Manufacturer;
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
                .Where(h => h.ManufacturerId == mfrId && h.IsActive)
                .Select(h => h.DescriptionId)
                .Distinct()
                .ToHashSet();
            filtered.AddRange(_descComboItems.Where(d => descIds.Contains(d.Id)));
        }

        _updatingDescCombo = true;
        try
        {
            DescCombo.ItemsSource = filtered;
            DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
            DescCombo.SelectedIndex = 0;
        }
        finally { _updatingDescCombo = false; }

        SearchHardware();
    }

    private void SearchHardware()
    {
        if (!_dbAvailable) return;

        var mfr      = MfrCombo.SelectedItem as Manufacturer;
        var descItem = DescCombo.SelectedItem as DescriptionComboItem;
        var model    = ModelBox.Text?.Trim();

        var mfrName = (mfr      == null || mfr.Id      == 0) ? null : mfr.ManufacturerName;
        var descId  = (descItem == null || descItem.Id == 0) ? (int?)null : descItem.Id;

        using var ctx = DatabaseInitializer.CreateContext();
        var results = new HardwareItemRepository(ctx)
            .Search(mfrName, descId, model)
            .Select(h => new HardwareItemDisplay(h))
            .ToList();

        ResultsList.ItemsSource = results;
        ResultsList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        if (results.Count > 0)
            ResultsList.SelectedIndex = 0;
        else
            ClearSelectedInfo();
    }

    private void OnResultSelected()
    {
        if (ResultsList.SelectedItem is HardwareItemDisplay d)
        {
            SelectedMfrLabel.Text   = $"Manufacturer: {d.Item.Manufacturer?.ManufacturerName}";
            SelectedDescLabel.Text  = $"Type: {d.Item.Description?.DescriptionText}";
            SelectedModelLabel.Text = $"Model: {d.Item.ModelNumber}";
            SelectedFreqLabel.Text  = $"Frequency: {d.Item.Frequency}";
        }
        else
        {
            ClearSelectedInfo();
        }
    }

    private void ClearSelectedInfo()
    {
        SelectedMfrLabel.Text   = string.Empty;
        SelectedDescLabel.Text  = string.Empty;
        SelectedModelLabel.Text = string.Empty;
        SelectedFreqLabel.Text  = string.Empty;
    }

    // ---------- Generate / Open PDF ----------

    private async Task OnOpenClickedAsync()
    {
        if (ResultsList.SelectedItem is not HardwareItemDisplay display)
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
            outputPath = await GeneratePdfAsync(display.Item, _cts.Token);
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

            var acquirer  = new FileAcquirer(new HttpClient(new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            }));
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

    // ---------- Display wrapper ----------

    /// <summary>Wraps a <see cref="HardwareItem"/> with a formatted display string.</summary>
    private sealed class HardwareItemDisplay
    {
        /// <summary>Gets the underlying hardware item.</summary>
        public HardwareItem Item { get; }

        /// <summary>Gets the text shown in the results listbox.</summary>
        public string DisplayText { get; }

        /// <summary>Initializes a new <see cref="HardwareItemDisplay"/>.</summary>
        public HardwareItemDisplay(HardwareItem item)
        {
            Item = item;
            var mfr  = item.Manufacturer?.ManufacturerName ?? "?";
            var desc = item.Description?.DescriptionText    ?? "?";
            DisplayText = $"{mfr} — {desc} — {item.ModelNumber}";
        }
    }
}
