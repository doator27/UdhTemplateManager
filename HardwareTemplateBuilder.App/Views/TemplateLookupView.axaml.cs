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
    private List<DescriptionComboItem> _leafDescComboItems = new();
    private CancellationTokenSource? _cts;
    private string _currentSearchText = string.Empty;

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

        var allDescriptions = new DescriptionRepository(context).GetAll();

        _manufacturers = new ManufacturerRepository(context)
            .GetAll().OrderBy(m => m.ManufacturerName).ToList();

        // Leaf-only — intermediate parent categories aren't meaningful hardware-item
        // descriptions on their own.
        _leafDescComboItems = DescriptionHelper.BuildLeafComboItems(allDescriptions);

        MfrList.ItemsSource = _manufacturers;
        MfrList.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        MfrList.SelectionChanged += (_, _) => { RefreshDescList(); RefreshModelList(); };
        DescList.SelectionChanged += (_, _) => RefreshModelList();
        ModelList.SelectionChanged += (_, _) => OnModelSelected();
        GenerateButton.Click += async (_, e) => await OnGenerateClickedAsync(e);
        SearchBox.TextChanged += (_, _) => OnSearchTextChanged();
        ClearSearchButton.Click += (_, _) => ClearSearch();

        // Show every description/model on first load; selections narrow both from there.
        RefreshDescList();
        RefreshModelList();
    }

    // ---------- Search ----------

    private void OnSearchTextChanged()
    {
        _currentSearchText = SearchBox.Text?.Trim() ?? string.Empty;
        RefreshDescList();
        RefreshModelList();
    }

    private void ClearSearch()
    {
        SearchBox.Text = string.Empty;
        _currentSearchText = string.Empty;
        RefreshDescList();
        RefreshModelList();
    }

    /// <summary>
    /// Repopulates the Description list to show only leaf descriptions that have at least one
    /// active hardware item under any of the currently selected manufacturers (all leaf
    /// descriptions if none are selected).
    /// </summary>
    private void RefreshDescList()
    {
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

        if (!string.IsNullOrWhiteSpace(_currentSearchText))
        {
            items = items.Where(d => d.DisplayText.Contains(_currentSearchText, StringComparison.OrdinalIgnoreCase)).ToList();
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

        if (!string.IsNullOrWhiteSpace(_currentSearchText))
        {
            query = query.Where(h => 
                h.Manufacturer!.ManufacturerName.Contains(_currentSearchText) ||
                h.Description!.DescriptionText.Contains(_currentSearchText) ||
                h.ModelNumber.Contains(_currentSearchText));
        }

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
            GenerateButton.IsEnabled = true;
        }
        else
        {
            SelectedMfrLabel.Text   = string.Empty;
            SelectedDescLabel.Text  = string.Empty;
            SelectedModelLabel.Text = string.Empty;
            SelectedFreqLabel.Text  = string.Empty;
            GenerateButton.IsEnabled = false;
        }
    }

    /// <summary>Returns the IDs of the currently selected items in a multi-select ListBox.</summary>
    private static HashSet<int> SelectedIds<T>(ListBox listBox, Func<T, int> idSelector) =>
        listBox.SelectedItems?.Cast<T>().Select(idSelector).ToHashSet() ?? new HashSet<int>();

    // ---------- Generate PDF ----------

    private async Task OnGenerateClickedAsync(RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not HardwareItem item)
        {
            StatusLabel.Text = "Please select a hardware item first.";
            return;
        }

        // Overwrite check: resolve expected output path and confirm if it already exists.
        {
            string saveLocationForCheck;
            using (var context = DatabaseInitializer.CreateContext())
            {
                saveLocationForCheck = new AppSettingRepository(context).GetValue("TemplateStorageLocation");
                if (string.IsNullOrWhiteSpace(saveLocationForCheck))
                    saveLocationForCheck = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var expectedPath = Path.Combine(
                saveLocationForCheck,
                QuickMergeService.BuildOutputFileName(item));

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
            GenerateButton.IsEnabled = true;
            GenerateProgress.IsVisible = false;
        }

        // Increment frequency on success.
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var freshItem = ctx.HardwareItems.Find(item.Id);
            if (freshItem != null)
                new FrequencyService(ctx).IncrementFrequency(freshItem);
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
        RefreshModelList();
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

    // ---------- Helpers ----------

    /// <summary>Posts a status message to the UI thread from any thread.</summary>
    private void PostStatus(string message) =>
        Dispatcher.UIThread.Post(() => StatusLabel.Text = message);
}
