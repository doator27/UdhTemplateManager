using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.EntityFrameworkCore;
using PDFtoImage;
using SkiaSharp;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// View that lets users search for a hardware item, preview its generated/merged PDF, and
/// generate/open it. Corresponds to Phase 9 of the implementation roadmap.
/// </summary>
public partial class TemplateLookupView : UserControl
{
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _leafDescComboItems = new();
    private CancellationTokenSource? _cts;

    // Maps every searchable label (custom job labels and hardware item model numbers) to the
    // hardware item it resolves to, deduplicated case-insensitively so suggestions and exact
    // lookup search the same combined set.
    private Dictionary<string, int> _searchableLabels = new(StringComparer.OrdinalIgnoreCase);

    // Guards against re-entrant cascading refreshes while combo ItemsSource is being swapped.
    private bool _suppressCascade;

    // PDF preview state.
    private byte[]? _previewPdfBytes;
    private string? _previewPdfPath;
    private int _previewPageCount;
    private int _previewPageIndex;
    private int? _previewItemId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view and wires events on load.</summary>
    public TemplateLookupView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    // ---------- Initialization ----------

    private async Task InitializeAsync()
    {
        using var context = DatabaseInitializer.CreateContext();

        var allDescriptions = new DescriptionRepository(context).GetAll();

        _manufacturers = new ManufacturerRepository(context)
            .GetAll().OrderBy(m => m.ManufacturerName).ToList();

        // Leaf-only — intermediate parent categories aren't meaningful hardware-item
        // descriptions on their own. Descriptions that have hardware items assigned to them
        // are always included even if they later gained child nodes (no longer true leaves),
        // so existing items' descriptions never silently disappear from the list.
        var descIdsInUse = context.HardwareItems
            .Where(h => h.IsActive && h.Description.IsActive)
            .Select(h => h.DescriptionId)
            .Distinct()
            .ToHashSet();
        _leafDescComboItems = DescriptionHelper.BuildSelectableComboItems(allDescriptions, descIdsInUse, activeOnly: true);

        // Build the combined searchable label set: unique custom labels actually typed on a
        // job (first occurrence wins, later duplicates skipped) take priority, then hardware
        // item model numbers fill in any remaining entries not already covered by a label.
        // Labels pointing at an inactive/missing hardware item are broken links (e.g. the
        // item was later deactivated or removed) and are filtered out so they never surface
        // as a "no exact match" result.
        var activeItemIds = context.HardwareItems
            .Where(h => h.IsActive && h.Description.IsActive)
            .Select(h => h.Id)
            .ToHashSet();

        var searchableLabels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var jh in context.JobHardware.Where(jh => jh.CustomDescription != null).OrderBy(jh => jh.Id))
        {
            if (!activeItemIds.Contains(jh.HardwareItemId)) continue;

            var label = jh.CustomDescription!.Trim();
            if (label.Length > 0 && !searchableLabels.ContainsKey(label))
                searchableLabels[label] = jh.HardwareItemId;
        }
        foreach (var h in context.HardwareItems.Where(h => h.IsActive && h.Description.IsActive))
        {
            if (!string.IsNullOrWhiteSpace(h.ModelNumber) && !searchableLabels.ContainsKey(h.ModelNumber))
                searchableLabels[h.ModelNumber] = h.Id;
        }
        _searchableLabels = searchableLabels;

        MfrCombo.ItemsSource = _manufacturers;
        MfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        MfrCombo.SelectionChanged += async (_, _) =>
        {
            if (_suppressCascade) return;

            var cleared = MfrCombo.SelectedItem is null;
            if (cleared)
            {
                // Clearing the Manufacturer combo clears every combo below it rather than
                // just re-filtering them while keeping their current selections.
                _suppressCascade = true;
                DescCombo.SelectedItem = null;
                _suppressCascade = false;
            }

            await RefreshDescComboAsync(preserveSelection: !cleared);
            await RefreshModelComboAsync(autoSelectFirst: !cleared);
        };
        DescCombo.SelectionChanged += async (_, _) =>
        {
            if (_suppressCascade) return;

            // Clearing the Description combo clears the Model # combo instead of leaving its
            // current selection in place.
            await RefreshModelComboAsync(autoSelectFirst: DescCombo.SelectedItem is not null);
        };
        ModelCombo.SelectionChanged += (_, _) => { if (!_suppressCascade) OnModelSelected(); };

        GenerateButton.Click += async (_, e) => await OnGenerateClickedAsync(e);
        LookupButton.Click += async (_, _) => await OnLookupClickedAsync();
        ClearButton.Click += async (_, _) => await ClearAllAsync();
        ExportLookupDataButton.Click += async (_, _) => await ExportLookupDataAsync();
        PrevPageButton.Click += async (_, _) => await ChangePreviewPageAsync(-1);
        NextPageButton.Click += async (_, _) => await ChangePreviewPageAsync(1);
        LabelLookupBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await OnLookupClickedAsync();
        };
        LabelLookupBox.TextChanged += (_, _) => RefreshLabelSuggestions();
        LabelSuggestionsList.SelectionChanged += (_, _) =>
        {
            if (LabelSuggestionsList.SelectedItem is string suggestion)
            {
                LabelLookupBox.Text = suggestion;
                LabelLookupBox.CaretIndex = suggestion.Length;
                LabelSuggestionsList.IsVisible = false;
                LabelSuggestionsList.SelectedItem = null;
                LabelLookupBox.Focus();
            }
        };

        PdfScrollViewer.SizeChanged += (_, _) => FitPreviewImageToViewportWidth();

        // Show every description/model on first load with nothing selected; selections narrow
        // both from there.
        await RefreshDescComboAsync();
        await RefreshModelComboAsync(autoSelectFirst: false);
    }

    // ---------- Cascading combo logic ----------

    /// <summary>
    /// Repopulates the Description combo to show only leaf descriptions that have at least one
    /// active hardware item under the currently selected manufacturer (all leaf descriptions if
    /// none is selected).
    /// </summary>
    private async Task RefreshDescComboAsync(bool preserveSelection = true)
    {
        var mfrId = (MfrCombo.SelectedItem as Manufacturer)?.Id;
        var previouslySelected = preserveSelection ? (DescCombo.SelectedItem as DescriptionComboItem)?.Id : null;

        List<DescriptionComboItem> items;
        if (mfrId is null)
        {
            items = _leafDescComboItems;
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = (await ctx.HardwareItems
                .Where(h => h.IsActive && h.Description.IsActive && h.ManufacturerId == mfrId)
                .Select(h => h.DescriptionId)
                .Distinct()
                .ToListAsync())
                .ToHashSet();
            items = _leafDescComboItems.Where(d => descIds.Contains(d.Id)).ToList();
        }

        _suppressCascade = true;
        DescCombo.ItemsSource = items;
        DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        DescCombo.SelectedItem = previouslySelected is null ? null : items.FirstOrDefault(d => d.Id == previouslySelected);
        _suppressCascade = false;
    }

    /// <summary>
    /// Repopulates the Model # combo with every hardware item matching the currently selected
    /// Manufacturer and Description — with nothing selected in either combo, every item is
    /// shown, narrowing only as selections are made.
    /// </summary>
    private async Task RefreshModelComboAsync(bool autoSelectFirst = true)
    {
        var mfrId = (MfrCombo.SelectedItem as Manufacturer)?.Id;
        var descId = (DescCombo.SelectedItem as DescriptionComboItem)?.Id;

        using var ctx = DatabaseInitializer.CreateContext();
        var query = ctx.HardwareItems
            .Include(h => h.Manufacturer)
            .Include(h => h.Description)
            .Where(h => h.IsActive && h.Description.IsActive)
            .AsQueryable();

        if (mfrId is not null) query = query.Where(h => h.ManufacturerId == mfrId);
        if (descId is not null) query = query.Where(h => h.DescriptionId == descId);

        var results = await query
            .OrderByDescending(h => h.Frequency)
            .ThenBy(h => h.ModelNumber)
            .ToListAsync();

        _suppressCascade = true;
        ModelCombo.ItemsSource = results;
        ModelCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
        ModelCombo.SelectedItem = null;
        _suppressCascade = false;

        if (results.Count > 0 && autoSelectFirst)
            ModelCombo.SelectedIndex = 0;
        else
            OnModelSelected();
    }

    private void OnModelSelected()
    {
        if (ModelCombo.SelectedItem is HardwareItem item)
        {
            SelectedMfrLabel.Text   = $"Manufacturer: {item.Manufacturer?.ManufacturerName}";
            SelectedDescLabel.Text  = $"Description: {item.Description?.DescriptionText}";
            SelectedModelLabel.Text = $"Model #: {item.ModelNumber}";
            SelectedFreqLabel.Text  = $"Used {item.Frequency} time(s)";
            GenerateButton.IsEnabled = true;

            // Generate the preview as soon as an item is selected; "Generate PDF" then just
            // opens the already-rendered file externally instead of regenerating it.
            _ = GeneratePreviewAsync(item);
        }
        else
        {
            SelectedMfrLabel.Text   = string.Empty;
            SelectedDescLabel.Text  = string.Empty;
            SelectedModelLabel.Text = string.Empty;
            SelectedFreqLabel.Text  = string.Empty;
            GenerateButton.IsEnabled = false;
            _previewItemId = null;
            ClearPreview();
        }
    }

    // ---------- Label suggestions ----------

    /// <summary>
    /// Repopulates <see cref="LabelSuggestionsList"/> with known job labels and hardware item
    /// model numbers that contain the currently typed text (case-insensitive substring match),
    /// hiding the list when the box is empty or there are no matches.
    /// </summary>
    private void RefreshLabelSuggestions()
    {
        var text = LabelLookupBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            LabelSuggestionsList.ItemsSource = null;
            LabelSuggestionsList.IsVisible = false;
            return;
        }

        var matches = _searchableLabels.Keys
            .Where(l => l.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        LabelSuggestionsList.ItemsSource = matches;
        LabelSuggestionsList.IsVisible = matches.Count > 0;
    }

    // ---------- Label lookup ----------

    /// <summary>
    /// Looks up a hardware item by an exact (case-insensitive) match against any custom label
    /// that has ever been used for it on a job, selects the matching combos, and generates its
    /// PDF preview.
    /// </summary>
    private async Task OnLookupClickedAsync()
    {
        var label = LabelLookupBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            StatusLabel.Text = "Enter a label to look up.";
            return;
        }

        HardwareItem? match;
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            int? hardwareItemId = _searchableLabels.TryGetValue(label, out var id)
                ? id
                : null;

            match = hardwareItemId is null
                ? null
                : await ctx.HardwareItems
                    .Include(h => h.Manufacturer)
                    .Include(h => h.Description)
                    .FirstOrDefaultAsync(h => h.Id == hardwareItemId && h.IsActive && h.Description.IsActive);
        }

        if (match is null)
        {
            StatusLabel.Text = $"No exact match found for label \"{label}\".";
            return;
        }

        StatusLabel.Text = string.Empty;

        // Select the matching Manufacturer/Description/Model # so the cascading combos reflect
        // the resolved item.
        _suppressCascade = true;
        MfrCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == match.ManufacturerId);
        _suppressCascade = false;
        await RefreshDescComboAsync();
        _suppressCascade = true;
        DescCombo.SelectedItem = (DescCombo.ItemsSource as IEnumerable<DescriptionComboItem>)?
            .FirstOrDefault(d => d.Id == match.DescriptionId);
        _suppressCascade = false;
        await RefreshModelComboAsync();

        var resolved = (ModelCombo.ItemsSource as IEnumerable<HardwareItem>)?
            .FirstOrDefault(h => h.Id == match.Id);
        ModelCombo.SelectedItem = resolved;
        OnModelSelected();
    }

    private async Task ClearAllAsync()
    {
        LabelLookupBox.Text = string.Empty;
        LabelSuggestionsList.ItemsSource = null;
        LabelSuggestionsList.IsVisible = false;

        _suppressCascade = true;
        MfrCombo.SelectedItem = null;
        DescCombo.SelectedItem = null;
        _suppressCascade = false;

        await RefreshDescComboAsync(preserveSelection: false);
        await RefreshModelComboAsync(autoSelectFirst: false);
        ClearPreview();
    }

    // ---------- Lookup data export ----------

    /// <summary>
    /// Re-exports the standalone Lookup app's <c>data.json</c> snapshot from the current
    /// database state, without requiring a full "Refresh Templates" run. Runs the DB query and
    /// file write on a background thread so the UI stays responsive while exporting.
    /// </summary>
    private async Task ExportLookupDataAsync()
    {
        ExportLookupDataButton.IsEnabled = false;
        StatusLabel.Text = "Updating Lookup app data...";

        try
        {
            var path = await Task.Run(() =>
            {
                using var ctx = DatabaseInitializer.CreateContext();
                return new LookupDataExportService(ctx).Export();
            });

            StatusLabel.Text = $"Lookup app data updated: {path}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to update Lookup app data: {ex.Message}";
        }
        finally
        {
            ExportLookupDataButton.IsEnabled = true;
        }
    }

    // ---------- Generate PDF ----------

    /// <summary>
    /// Opens the already-rendered preview PDF with the OS default viewer (regenerating it first
    /// if it isn't up to date with the currently selected item), and increments the item's usage
    /// frequency — unlike preview generation, which happens automatically on every selection and
    /// should not affect frequency.
    /// </summary>
    private async Task OnGenerateClickedAsync(RoutedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not HardwareItem item)
        {
            StatusLabel.Text = "Please select a hardware item first.";
            return;
        }

        if (_previewItemId != item.Id || _previewPdfPath is null || !File.Exists(_previewPdfPath))
            await GeneratePreviewAsync(item);

        if (_previewPdfPath is null || !File.Exists(_previewPdfPath))
        {
            StatusLabel.Text = "Could not generate the PDF.";
            return;
        }

        // Increment frequency only when the user actually opens the file, not on every preview.
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var freshItem = ctx.HardwareItems.Find(item.Id);
            if (freshItem != null)
                new FrequencyService(ctx).IncrementFrequency(freshItem);
        }

        try
        {
            Process.Start(new ProcessStartInfo(_previewPdfPath) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal: file exists but couldn't be auto-opened.
        }

        StatusLabel.Text = $"Opened: {Path.GetFileName(_previewPdfPath)}";

        // Refresh results so updated frequency ordering is reflected.
        await RefreshModelComboAsync();
    }

    /// <summary>
    /// Generates (or regenerates) the merged PDF for <paramref name="item"/> and loads its first
    /// page into the in-app previewer. Does not open an external viewer or touch usage
    /// frequency — called automatically whenever a hardware item is selected.
    /// </summary>
    private async Task GeneratePreviewAsync(HardwareItem item)
    {
        // Cancel any in-progress generation.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        GenerateButton.IsEnabled = false;
        GenerateProgress.IsVisible = true;
        StatusLabel.Text = "Generating preview...";

        string outputPath;
        try
        {
            outputPath = await GeneratePdfAsync(item, ct);
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

        _previewItemId = item.Id;
        StatusLabel.Text = string.Empty;
        LoadPreview(outputPath);
    }

    /// <summary>
    /// Resolves the save location, then delegates to the shared <see cref="QuickMergeService"/>
    /// pipeline (loads templates, sorts, acquires/processes each PDF, merges) — the same
    /// pipeline used by the standalone Lookup app's "Open Templates" button.
    /// </summary>
    private async Task<string> GeneratePdfAsync(HardwareItem item, CancellationToken ct)
    {
        string saveLocation;
        using (var context = DatabaseInitializer.CreateContext())
        {
            saveLocation = new AppSettingRepository(context).GetValue("TemplateStorageLocation");
            if (string.IsNullOrWhiteSpace(saveLocation))
                saveLocation = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        using var mergeContext = DatabaseInitializer.CreateContext();
        var socketsHandler = new System.Net.Http.SocketsHttpHandler();
        socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var httpClient = new HttpClient(socketsHandler);

        var progress = new Progress<string>(PostStatus);
        var service = new QuickMergeService(mergeContext, httpClient);
        return await service.GenerateAsync(item, saveLocation, "htb", progress, ct);
    }

    // ---------- PDF preview ----------

    /// <summary>Loads the PDF at <paramref name="pdfPath"/> and renders its first page.</summary>
    private async void LoadPreview(string pdfPath)
    {
        try
        {
            _previewPdfPath = pdfPath;
            _previewPdfBytes = File.ReadAllBytes(pdfPath);
            _previewPageCount = Conversion.GetPageCount(_previewPdfBytes);
            _previewPageIndex = 0;
            await RenderPreviewPageAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not preview PDF: {ex.Message}";
            ClearPreview();
        }
    }

    private void ClearPreview()
    {
        _previewPdfBytes = null;
        _previewPdfPath = null;
        _previewPageCount = 0;
        _previewPageIndex = 0;
        PdfPreviewImage.Source = null;
        PageLabel.Text = "No document loaded";
        PrevPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
    }

    private async Task ChangePreviewPageAsync(int delta)
    {
        if (_previewPdfBytes is null) return;

        var newIndex = _previewPageIndex + delta;
        if (newIndex < 0 || newIndex >= _previewPageCount) return;

        _previewPageIndex = newIndex;
        await RenderPreviewPageAsync();
    }

    private async Task RenderPreviewPageAsync()
    {
        if (_previewPdfBytes is null) return;

        var pdfBytes = _previewPdfBytes;
        var pageIndex = _previewPageIndex;

        // Rasterizing a PDF page is CPU-heavy; run it on a background thread so the UI thread
        // (and button clicks) stay responsive instead of freezing for the duration.
        var pngBytes = await Task.Run(() =>
        {
            using SKBitmap skBitmap = Conversion.ToImage(pdfBytes, new Index(pageIndex));
            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        });

        using var stream = new MemoryStream(pngBytes);
        PdfPreviewImage.Source = new Bitmap(stream);

        PageLabel.Text = $"Page {_previewPageIndex + 1} of {_previewPageCount}";
        PrevPageButton.IsEnabled = _previewPageIndex > 0;
        NextPageButton.IsEnabled = _previewPageIndex < _previewPageCount - 1;

        FitPreviewImageToViewportWidth();
    }

    /// <summary>
    /// Constrains the preview image to the scroll viewport's width (letting height grow
    /// proportionally) so the page defaults to fitting the available width; pages taller than
    /// the viewport simply continue scrolling vertically instead of being shrunk to fit.
    /// </summary>
    private void FitPreviewImageToViewportWidth()
    {
        if (PdfPreviewImage.Source is null) return;

        var viewportWidth = PdfScrollViewer.Viewport.Width;
        if (viewportWidth > 0)
            PdfPreviewImage.Width = viewportWidth;
    }

    // ---------- Helpers ----------

    /// <summary>Posts a status message to the UI thread from any thread.</summary>
    private void PostStatus(string message) =>
        Dispatcher.UIThread.Post(() => StatusLabel.Text = message);
}
