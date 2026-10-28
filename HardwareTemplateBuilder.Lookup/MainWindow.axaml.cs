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
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Core.Services.Pdf;
using PDFtoImage;
using SkiaSharp;

namespace HardwareTemplateBuilder.Lookup;

/// <summary>
/// Standalone template lookup window. Reads hardware item/template metadata from the
/// <c>data.json</c> snapshot (exported by the main app's "Refresh Templates" action) instead of
/// connecting to the shared database directly. Mirrors the in-app Template Lookup view's UI and
/// behavior: cascading Manufacturer/Description/Model# search, direct model-number lookup, and
/// an in-window PDF preview with page navigation and copy-to-clipboard. Read-only: never writes
/// to the database.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LookupDataLoader _data = new();

    private List<DescriptionComboItem> _leafDescComboItems = new();
    private CancellationTokenSource? _cts;
    private bool _dataAvailable;

    // Combined searchable label set: custom job labels and every active hardware item's model
    // number, mapped to the resolved hardware item.
    private Dictionary<string, HardwareItem> _searchableLabels = new(StringComparer.OrdinalIgnoreCase);

    // Guards against re-entrant cascading refreshes while combo ItemsSource is being swapped.
    private bool _suppressCascade;

    // PDF preview state.
    private byte[]? _previewPdfBytes;
    private string? _previewPdfPath;
    private int _previewPageCount;
    private int _previewPageIndex;
    private int? _previewItemId;

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
        if (!_data.TryLoad(out var error))
        {
            StatusLabel.Text = error;
            return;
        }

        _dataAvailable = true;

        // Leaf-only — intermediate parent categories aren't meaningful hardware-item
        // descriptions on their own. Descriptions that have hardware items assigned to them
        // are always included even if they later gained child nodes (no longer true leaves),
        // so existing items' descriptions never silently disappear from the list.
        var descIdsInUse = _data.HardwareItems
            .Where(h => h.IsActive && h.Description.IsActive)
            .Select(h => h.DescriptionId)
            .Distinct()
            .ToHashSet();
        _leafDescComboItems = DescriptionHelper.BuildSelectableComboItems(_data.Descriptions, descIdsInUse, activeOnly: true);

        // Combine job-assigned custom labels (unique, first occurrence wins) with hardware item
        // model numbers filling in any remaining entries not already covered by a label.
        // Labels pointing at an inactive/missing hardware item are broken links and are
        // filtered out so they never surface as a "no exact match" result.
        var searchableLabels = new Dictionary<string, HardwareItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _data.LabelsToHardwareItem)
        {
            if (!kvp.Value.IsActive || !kvp.Value.Description.IsActive) continue;
            if (!searchableLabels.ContainsKey(kvp.Key))
                searchableLabels[kvp.Key] = kvp.Value;
        }
        foreach (var h in _data.HardwareItems.Where(h => h.IsActive && h.Description.IsActive))
        {
            if (!string.IsNullOrWhiteSpace(h.ModelNumber) && !searchableLabels.ContainsKey(h.ModelNumber))
                searchableLabels[h.ModelNumber] = h;
        }
        _searchableLabels = searchableLabels;

        MfrCombo.ItemsSource = _data.Manufacturers;
        MfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        MfrCombo.SelectionChanged += (_, _) =>
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

            RefreshDescCombo(preserveSelection: !cleared);
            RefreshModelCombo(autoSelectFirst: !cleared);
        };
        DescCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressCascade) return;

            // Clearing the Description combo clears the Model # combo instead of leaving its
            // current selection in place.
            RefreshModelCombo(autoSelectFirst: DescCombo.SelectedItem is not null);
        };
        ModelCombo.SelectionChanged += (_, _) => { if (!_suppressCascade) OnModelSelected(); };

        GenerateButton.Click += async (_, _) => await OnGenerateClickedAsync();
        LookupButton.Click += async (_, _) => await OnLookupClickedAsync();
        ClearButton.Click += (_, _) => ClearAll();
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
        RefreshDescCombo();
        RefreshModelCombo(autoSelectFirst: false);
    }

    // ---------- Cascading combo logic ----------

    /// <summary>
    /// Repopulates the Description combo to show only leaf descriptions that have at least one
    /// active hardware item under the currently selected manufacturer (all leaf descriptions if
    /// none is selected).
    /// </summary>
    private void RefreshDescCombo(bool preserveSelection = true)
    {
        if (!_dataAvailable) return;

        var mfrId = (MfrCombo.SelectedItem as Manufacturer)?.Id;
        var previouslySelected = preserveSelection ? (DescCombo.SelectedItem as DescriptionComboItem)?.Id : null;

        List<DescriptionComboItem> items;
        if (mfrId is null)
        {
            items = _leafDescComboItems;
        }
        else
        {
            var descIds = _data.HardwareItems
                .Where(h => h.IsActive && h.Description.IsActive && h.ManufacturerId == mfrId)
                .Select(h => h.DescriptionId)
                .Distinct()
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
    private void RefreshModelCombo(bool autoSelectFirst = true)
    {
        if (!_dataAvailable) return;

        var mfrId = (MfrCombo.SelectedItem as Manufacturer)?.Id;
        var descId = (DescCombo.SelectedItem as DescriptionComboItem)?.Id;

        var query = _data.HardwareItems
            .Where(h => h.IsActive && h.Description.IsActive)
            .AsEnumerable();

        if (mfrId is not null) query = query.Where(h => h.ManufacturerId == mfrId);
        if (descId is not null) query = query.Where(h => h.DescriptionId == descId);

        var results = query
            .OrderByDescending(h => h.Frequency)
            .ThenBy(h => h.ModelNumber)
            .ToList();

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
            SelectedMfrLabel.Text = $"Manufacturer: {item.Manufacturer?.ManufacturerName}";
            SelectedDescLabel.Text = $"Description: {item.Description?.DescriptionText}";
            SelectedModelLabel.Text = $"Model #: {item.ModelNumber}";
            SelectedFreqLabel.Text = $"Used {item.Frequency} time(s)";
            GenerateButton.IsEnabled = true;
            _ = GeneratePreviewAsync(item);
        }
        else
        {
            SelectedMfrLabel.Text = string.Empty;
            SelectedDescLabel.Text = string.Empty;
            SelectedModelLabel.Text = string.Empty;
            SelectedFreqLabel.Text = string.Empty;
            GenerateButton.IsEnabled = false;
            _previewItemId = null;
            ClearPreview();
        }
    }

    // ---------- Label lookup ----------

    /// <summary>
    /// Repopulates <see cref="LabelSuggestionsList"/> with known job labels and hardware item
    /// model numbers from the snapshot that contain the currently typed text (case-insensitive
    /// substring match), hiding the list when the box is empty or there are no matches.
    /// </summary>
    private void RefreshLabelSuggestions()
    {
        var text = LabelLookupBox.Text?.Trim();
        if (!_dataAvailable || string.IsNullOrWhiteSpace(text))
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

    /// <summary>
    /// Looks up a hardware item by an exact (case-insensitive) match against any custom label
    /// that has ever been used for it on a job, selects the matching combos, and generates its
    /// PDF preview.
    /// </summary>
    private async Task OnLookupClickedAsync()
    {
        if (!_dataAvailable) return;

        var label = LabelLookupBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            StatusLabel.Text = "Enter a label to look up.";
            return;
        }

        if (!_searchableLabels.TryGetValue(label, out var match) ||
            !match.IsActive || !match.Description.IsActive)
        {
            StatusLabel.Text = $"No exact match found for label \"{label}\".";
            return;
        }

        StatusLabel.Text = string.Empty;

        // Select the matching Manufacturer/Description so the cascading combos reflect the
        // resolved item, then select the matching Model #.
        _suppressCascade = true;
        MfrCombo.SelectedItem = _data.Manufacturers.FirstOrDefault(m => m.Id == match.ManufacturerId);
        _suppressCascade = false;
        RefreshDescCombo();
        _suppressCascade = true;
        DescCombo.SelectedItem = (DescCombo.ItemsSource as IEnumerable<DescriptionComboItem>)?
            .FirstOrDefault(d => d.Id == match.DescriptionId);
        _suppressCascade = false;
        RefreshModelCombo();

        var resolved = (ModelCombo.ItemsSource as IEnumerable<HardwareItem>)?
            .FirstOrDefault(h => h.Id == match.Id);
        ModelCombo.SelectedItem = resolved;
        OnModelSelected();
    }

    private void ClearAll()
    {
        LabelLookupBox.Text = string.Empty;
        StatusLabel.Text = string.Empty;
        LabelSuggestionsList.ItemsSource = null;
        LabelSuggestionsList.IsVisible = false;

        _suppressCascade = true;
        MfrCombo.SelectedItem = null;
        DescCombo.SelectedItem = null;
        _suppressCascade = false;

        RefreshDescCombo(preserveSelection: false);
        RefreshModelCombo(autoSelectFirst: false);
        ClearPreview();
    }

    // ---------- Generate PDF ----------

    /// <summary>
    /// Opens the already-rendered preview PDF with the OS default viewer (regenerating it first
    /// if it isn't up to date with the currently selected item).
    /// </summary>
    private async Task OnGenerateClickedAsync()
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

        try
        {
            Process.Start(new ProcessStartInfo(_previewPdfPath) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal: file exists but couldn't be auto-opened.
        }

        StatusLabel.Text = $"Opened: {Path.GetFileName(_previewPdfPath)}";
    }

    /// <summary>
    /// Generates (or regenerates) the merged PDF for <paramref name="item"/> and loads its first
    /// page into the in-app previewer. Does not open an external viewer — called automatically
    /// whenever a hardware item is selected.
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
    /// Delegates to the shared <see cref="QuickMergeService"/> pipeline (sorts, acquires/
    /// processes each PDF, merges) using the hardware item's templates loaded from the JSON
    /// snapshot — the same pipeline used by the in-app Template Lookup view's "Generate PDF"
    /// button.
    /// </summary>
    private async Task<string> GeneratePdfAsync(HardwareItem item, CancellationToken ct)
    {
        var saveLocation = !string.IsNullOrWhiteSpace(_data.TemplateStorageLocation)
            ? _data.TemplateStorageLocation
            : Path.GetTempPath();

        var templates = item.HardwareItemTemplates
            .Select(hit => hit.IndividualTemplate)
            .ToList();
        var allDescriptions = _data.Descriptions.ToDictionary(d => d.Id);

        var socketsHandler = new System.Net.Http.SocketsHttpHandler();
        socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var httpClient = new HttpClient(socketsHandler);

        var progress = new Progress<string>(PostStatus);
        var service = new QuickMergeService(httpClient);
        return await service.GenerateAsync(item, templates, allDescriptions, saveLocation, "htb_lookup", progress, ct);
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
