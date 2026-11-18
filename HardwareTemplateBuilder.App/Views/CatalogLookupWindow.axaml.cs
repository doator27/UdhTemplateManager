using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Catalogs;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Looks a callout up in the manufacturer's product catalogs (<see cref="CatalogLookupService"/>):
/// lists the matching pages with a preview of each, the template numbers they give and what the
/// option codes mean. When opened with <c>allowChoose</c>, "Use This Template #" / "Link This
/// Template" return a <see cref="CatalogLookupChoice"/>; otherwise it only shows the catalog.
/// </summary>
public partial class CatalogLookupWindow : Window
{
    private static readonly HttpClient Http = TemplateFinderService.CreateHttpClient();

    private readonly int _manufacturerId;
    private readonly bool _allowChoose;
    private readonly CatalogLookupService _lookup = new(DatabaseInitializer.CreateContext);
    private readonly ProductCatalogService _catalogs = new(DatabaseInitializer.CreateContext, Http);
    private CatalogLookupResult? _result;
    private CancellationTokenSource? _previewCts;

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public CatalogLookupWindow() : this(0, string.Empty, string.Empty, false) { }

    /// <summary>Creates the window.</summary>
    /// <param name="manufacturerId">The manufacturer whose catalogs to search.</param>
    /// <param name="manufacturerName">Its name.</param>
    /// <param name="callout">The callout or model number.</param>
    /// <param name="allowChoose">Show the buttons that hand a template number back.</param>
    public CatalogLookupWindow(int manufacturerId, string manufacturerName, string callout, bool allowChoose)
    {
        _manufacturerId = manufacturerId;
        _allowChoose = allowChoose;
        InitializeComponent();
        Title = $"Look Up in Product Catalog - {manufacturerName} {callout}".Trim();
        HeaderText.Text = $"Product catalog: {manufacturerName}";
        CalloutBox.Text = callout;
        UseButton.IsVisible = LinkButton.IsVisible = allowChoose;

        LookUpButton.Click += async (_, _) => await LookUpAsync();
        HitsList.SelectionChanged += async (_, _) => await ShowHitAsync();
        TemplatesList.SelectionChanged += (_, _) => UpdateButtons();
        UseButton.Click += (_, _) => Choose(link: false);
        LinkButton.Click += (_, _) => Choose(link: true);
        CloseButton.Click += (_, _) => Close(null);
        Opened += async (_, _) => await LookUpAsync();
        Closed += (_, _) => _previewCts?.Cancel();
    }

    private async Task LookUpAsync()
    {
        var callout = (CalloutBox.Text ?? string.Empty).Trim();
        LookUpButton.IsEnabled = false;
        StatusLabel.Foreground = AppColors.Muted;
        StatusLabel.Text = "Searching the catalogs...";
        try
        {
            _result = await Task.Run(() => _lookup.Lookup(_manufacturerId, callout));
            SummaryText.Text = _result.Summary;
            HitsList.ItemsSource = _result.Hits;
            HitsList.SelectedItem = _result.Hits.FirstOrDefault();
            StatusLabel.Text = _result.Found ? "Select a page to see it." : string.Empty;
            if (!_result.Found) await PreviewPanel.ShowPdfAsync(null);
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            LookUpButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    private async Task ShowHitAsync()
    {
        var hit = HitsList.SelectedItem as CatalogHit;
        TemplatesList.ItemsSource = hit?.Templates;
        TemplatesList.SelectedItem = hit?.Templates.FirstOrDefault();
        SnippetBox.Text = hit?.Snippet ?? string.Empty;
        UpdateButtons();

        _previewCts?.Cancel();
        if (hit == null)
        {
            await PreviewPanel.ShowPdfAsync(null);
            return;
        }
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        try
        {
            StatusLabel.Foreground = AppColors.Muted;
            StatusLabel.Text = $"Loading page {hit.PageNumber}...";
            var bytes = await Task.Run(() => _catalogs.PagePdfAsync(hit.CatalogId, hit.PageNumber, cts.Token));
            if (cts.IsCancellationRequested) return;
            await PreviewPanel.ShowPdfAsync(bytes);
            StatusLabel.Text = $"{hit.CatalogTitle}, page {hit.PageNumber}.";
        }
        catch (OperationCanceledException)
        {
            // another page was selected
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            await PreviewPanel.ShowPdfAsync(null);
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Couldn't show the page: {ex.Message}";
        }
    }

    private CatalogTemplateRef? SelectedTemplate =>
        TemplatesList.SelectedItem as CatalogTemplateRef ?? _result?.Templates.FirstOrDefault();

    private void UpdateButtons()
    {
        var t = SelectedTemplate;
        UseButton.IsEnabled = _allowChoose && t != null;
        LinkButton.IsEnabled = _allowChoose && t?.TemplateId != null;
    }

    private void Choose(bool link)
    {
        var t = SelectedTemplate;
        if (t == null || _result == null) return;
        var description = (HitsList.SelectedItem as CatalogHit)?.Description ?? _result.Description;
        Close(new CatalogLookupChoice(t.Number, link ? t.TemplateId : null, _result.SuggestedDescriptionId, description));
    }
}
