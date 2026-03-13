using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
/// View that lets users search for a hardware item and generate a merged PDF of all its
/// linked templates. Corresponds to Phase 9 of the implementation roadmap.
/// </summary>
public partial class TemplateLookupView : UserControl
{
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _descriptions = new();
    private CancellationTokenSource? _cts;
    private bool _updatingDescCombo;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view and wires events on load.</summary>
    public TemplateLookupView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    // ---------- Initialization ----------

    private void Initialize()
    {
        using var context = DatabaseInitializer.CreateContext();

        _manufacturers = new ManufacturerRepository(context)
            .GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descriptions = new DescriptionRepository(context)
            .GetAll().OrderBy(d => d.DescriptionText).ToList();

        // Populate Manufacturer combo with "(Any)" sentinel at index 0.
        var anyMfr = new List<Manufacturer> { new() { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        MfrCombo.ItemsSource = anyMfr;
        MfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        MfrCombo.SelectedIndex = 0;

        // Populate Description combo with "(Any)" sentinel at index 0.
        var anyDesc = new List<Description> { new() { Id = 0, DescriptionText = "(Any)" } };
        anyDesc.AddRange(_descriptions);
        DescCombo.ItemsSource = anyDesc;
        DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
        DescCombo.SelectedIndex = 0;

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");

        // Wire search controls — manufacturer drives description cascade.
        MfrCombo.SelectionChanged  += (_, _) => OnSearchMfrChanged();
        DescCombo.SelectionChanged += (_, _) => { if (!_updatingDescCombo) SearchHardware(); };
        ModelBox.TextChanged       += (_, _) => SearchHardware();

        ResultsList.SelectionChanged += (_, _) => OnResultSelected();
        GenerateButton.Click += async (_, e) => await OnGenerateClickedAsync(e);

        // Show all items on first load.
        SearchHardware();
    }

    // ---------- Search ----------

    /// <summary>
    /// Repopulates the Description combo to show only descriptions that have at least one
    /// hardware item made by the selected manufacturer, then re-runs the search.
    /// </summary>
    private void OnSearchMfrChanged()
    {
        var mfr = MfrCombo.SelectedItem as Manufacturer;
        var mfrId = mfr?.Id ?? 0;

        var filtered = new List<Description> { new() { Id = 0, DescriptionText = "(Any)" } };

        if (mfrId == 0)
        {
            filtered.AddRange(_descriptions);
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = ctx.HardwareItems
                .Where(h => h.ManufacturerId == mfrId)
                .Select(h => h.DescriptionId)
                .Distinct()
                .ToHashSet();
            filtered.AddRange(_descriptions.Where(d => descIds.Contains(d.Id)));
        }

        _updatingDescCombo = true;
        try
        {
            DescCombo.ItemsSource = filtered;
            DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
            DescCombo.SelectedIndex = 0;
        }
        finally
        {
            _updatingDescCombo = false;
        }

        SearchHardware();
    }

    private void SearchHardware()
    {
        var mfr = MfrCombo.SelectedItem as Manufacturer;
        var desc = DescCombo.SelectedItem as Description;
        var model = ModelBox.Text?.Trim();

        var mfrName  = (mfr  == null || mfr.Id  == 0) ? null : mfr.ManufacturerName;
        var descText = (desc == null || desc.Id == 0) ? null : desc.DescriptionText;

        using var context = DatabaseInitializer.CreateContext();
        var results = new HardwareItemRepository(context)
            .Search(mfrName, descText, model)
            .Select(h => new HardwareItemDisplay(h))
            .ToList();

        ResultsList.ItemsSource = results;
        ResultsList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        // Default to first item.
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

    // ---------- Generate PDF ----------

    private async Task OnGenerateClickedAsync(RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not HardwareItemDisplay display)
        {
            StatusLabel.Text = "Please select a hardware item first.";
            return;
        }

        // Overwrite check: resolve expected output path and confirm if it already exists.
        {
            string saveLocationForCheck;
            using (var context = DatabaseInitializer.CreateContext())
            {
                var profile = context.UserProfiles.FirstOrDefault();
                saveLocationForCheck = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
                    ? profile.DefaultTemplateSaveLocation
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var expectedPath = Path.Combine(
                saveLocationForCheck,
                $"{SanitizeFileName(display.Item.ModelNumber)}_templates.pdf");

            if (File.Exists(expectedPath))
            {
                var win = TopLevel.GetTopLevel(this) as Window;
                if (win != null)
                {
                    var overwrite = await DialogHelper.ConfirmAsync(win,
                        $"A file already exists:\n{Path.GetFileName(expectedPath)}\n\nOverwrite it?",
                        "File Already Exists");
                    if (!overwrite) return;
                }
            }
        }

        // Cancel any in-progress generation.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        GenerateButton.IsEnabled = false;
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
            GenerateButton.IsEnabled = true;
            GenerateProgress.IsVisible = false;
        }

        // Increment frequency on success.
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var item = ctx.HardwareItems.Find(display.Item.Id);
            if (item != null)
                new FrequencyService(ctx).IncrementFrequency(item);
        }

        // Open with OS default PDF viewer.
        try
        {
            Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal: file was generated but couldn't be auto-opened.
        }

        StatusLabel.Text = $"Saved: {Path.GetFileName(outputPath)}";

        // Refresh results so updated frequency ordering is reflected.
        SearchHardware();
    }

    /// <summary>
    /// Loads templates, sorts them, acquires/processes each PDF, merges, stamps page
    /// numbers, and saves to the active user's save location.
    /// Heavy work is pushed onto the thread pool; status updates are posted to the UI thread.
    /// </summary>
    private async Task<string> GeneratePdfAsync(HardwareItem item, CancellationToken ct)
    {
        // Load all data needed for generation on the calling thread (fast DB query).
        List<IndividualTemplate> templates;
        string saveLocation;

        using (var context = DatabaseInitializer.CreateContext())
        {
            templates = context.HardwareItemTemplates
                .Include(hit => hit.IndividualTemplate)
                    .ThenInclude(t => t.Manufacturer)
                .Include(hit => hit.IndividualTemplate)
                    .ThenInclude(t => t.Description)
                .Where(hit => hit.HardwareItemId == item.Id)
                .Select(hit => hit.IndividualTemplate)
                .ToList();

            var profile = context.UserProfiles.FirstOrDefault();
            saveLocation = !string.IsNullOrWhiteSpace(profile?.DefaultTemplateSaveLocation)
                ? profile.DefaultTemplateSaveLocation
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (templates.Count == 0)
            throw new InvalidOperationException(
                "This hardware item has no linked templates. Link templates via the Hardware Items screen.");

        // Sort by weight (single-item lookup: sort is purely by weight ascending).
        var sortedTemplates = new TemplateSorter(
            new WeightTemplateSortStrategy(new WeightParser())).Sort(templates);

        // Run heavy PDF work on a thread-pool thread to keep the UI responsive.
        return await Task.Run(async () =>
        {
            var workDir = Path.Combine(
                Path.GetTempPath(), $"htb_{item.Id}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);

            var acquirer  = new FileAcquirer(new HttpClient());
            var parser    = new PageRangeParser();
            var extractor = new PageExtractor();
            var rotator   = new PageRotator();
            var merger    = new PdfMerger();
            var numberer  = new PageNumberer();

            var processedPdfs = new List<string>();

            foreach (var template in sortedTemplates)
            {
                ct.ThrowIfCancellationRequested();

                PostStatus($"Acquiring {template.TemplateNumber}...");
                var acquired = await acquirer.AcquireAsync(template, workDir);

                PostStatus($"Processing {template.TemplateNumber}...");
                var pageNumbers = parser.Parse(template.PagesToPrint);
                var extracted = Path.Combine(workDir, $"ex_{template.Id}.pdf");
                extractor.Extract(acquired, pageNumbers, extracted);

                string processed = extracted;
                if (!string.IsNullOrWhiteSpace(template.PagesToRotate))
                {
                    var rotateOrig = parser.Parse(template.PagesToRotate);

                    // Map original page numbers to 1-based indices in the extracted PDF.
                    var indexMap = pageNumbers
                        .Select((p, i) => (Orig: p, Idx: i + 1))
                        .ToDictionary(x => x.Orig, x => x.Idx);

                    var rotateIdx = rotateOrig
                        .Where(p => indexMap.ContainsKey(p))
                        .Select(p => indexMap[p])
                        .ToList();

                    if (rotateIdx.Count > 0)
                    {
                        processed = Path.Combine(workDir, $"rot_{template.Id}.pdf");
                        rotator.Rotate(extracted, rotateIdx, template.RotationDirection, processed);
                    }
                }

                processedPdfs.Add(processed);
            }

            ct.ThrowIfCancellationRequested();

            PostStatus("Merging PDFs...");
            var merged = Path.Combine(workDir, "merged.pdf");
            merger.Merge(processedPdfs, merged);

            PostStatus("Stamping page numbers...");
            Directory.CreateDirectory(saveLocation);
            var safeName = SanitizeFileName(item.ModelNumber);
            var outputPath = Path.Combine(saveLocation, $"{safeName}_templates.pdf");
            numberer.StampPageNumbers(merged, skipPages: 0, outputPath);

            return outputPath;
        }, ct);
    }

    // ---------- Helpers ----------

    /// <summary>Posts a status message to the UI thread from any thread.</summary>
    private void PostStatus(string message) =>
        Dispatcher.UIThread.Post(() => StatusLabel.Text = message);

    /// <summary>Replaces characters that are invalid in file names with underscores.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // ---------- Inner display wrapper ----------

    /// <summary>
    /// Wraps a <see cref="HardwareItem"/> with a formatted display string for the results
    /// listbox.
    /// </summary>
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
