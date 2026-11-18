using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Catalogs;
using HardwareTemplateBuilder.Core.Services.Import;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>
/// Maintenance → Product Catalogs: each manufacturer's catalog PDFs (local files or online
/// links). Adding one reads its text (<see cref="ProductCatalogService.IndexAsync"/>) so the
/// template lookups can search it; "Search Website" looks for catalogs on the manufacturer's site.
/// </summary>
public partial class ProductCatalogsView : UserControl
{
    /// <summary>The route of this view.</summary>
    public const string Route = "ProductCatalogs";

    private static readonly HttpClient Http = TemplateFinderService.CreateHttpClient();

    private readonly ProductCatalogService _service = new(DatabaseInitializer.CreateContext, Http);
    private CancellationTokenSource? _cts;

    /// <summary>Raised to navigate elsewhere.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Creates the view.</summary>
    public ProductCatalogsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private sealed record CatalogRow(ProductCatalog Catalog)
    {
        public override string ToString() => ProductCatalogService.DisplayText(Catalog);
    }

    private sealed record FoundRow(string Url, int Score)
    {
        public override string ToString() => $"{ProductCatalogService.TitleFromLink(Url)}   {Url}";
    }

    private int? ManufacturerId => ManufacturerCombo.SelectedItem is Manufacturer m ? m.Id : null;

    private string ManufacturerName => (ManufacturerCombo.SelectedItem as Manufacturer)?.ManufacturerName ?? string.Empty;

    private void Initialize()
    {
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            ManufacturerCombo.ItemsSource = ctx.Manufacturers.OrderBy(m => m.ManufacturerName).ToList();
        }
        ManufacturerCombo.SelectionChanged += (_, e) => { e.Handled = true; FoundList.ItemsSource = null; LoadCatalogs(); };

        AddFileButton.Click += async (_, _) => await AddFileAsync();
        AddLinkButton.Click += async (_, _) => await AddLinksAsync(new[] { (LinkBox.Text ?? string.Empty).Trim() });
        ReadButton.Click += async (_, _) => { if (CatalogList.SelectedItem is CatalogRow r) await IndexAsync(new[] { r.Catalog.Id }); };
        RenameButton.Click += async (_, _) => await RenameAsync();
        OpenButton.Click += async (_, _) => await OpenAsync();
        RemoveButton.Click += async (_, _) => await RemoveAsync();
        FindOnlineButton.Click += async (_, _) => await FindOnlineAsync();
        AddFoundButton.Click += async (_, _) =>
            await AddLinksAsync(FoundList.SelectedItems.OfType<FoundRow>().Select(r => r.Url).ToList());
        FoundList.SelectionChanged += (_, e) => { e.Handled = true; AddFoundButton.IsEnabled = _cts == null && FoundList.SelectedItems.Count > 0; };
        GoogleButton.Click += (_, _) => { if (ManufacturerId != null) OpenShell(ProductCatalogService.GoogleSearchUrl(ManufacturerName)); };
        CancelButton.Click += (_, _) => _cts?.Cancel();
        UpdateButtons();
    }

    private void LoadCatalogs(int? select = null)
    {
        var id = ManufacturerId;
        CatalogList.ItemsSource = id is int m ? _service.ForManufacturer(m).Select(c => new CatalogRow(c)).ToList() : null;
        if (select != null && CatalogList.ItemsSource is List<CatalogRow> rows)
            CatalogList.SelectedItem = rows.FirstOrDefault(r => r.Catalog.Id == select);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = _cts == null;
        bool mfr = ManufacturerId != null;
        AddFileButton.IsEnabled = AddLinkButton.IsEnabled = FindOnlineButton.IsEnabled = GoogleButton.IsEnabled = idle && mfr;
        ReadButton.IsEnabled = RenameButton.IsEnabled = OpenButton.IsEnabled = RemoveButton.IsEnabled = idle && CatalogList.SelectedItem != null;
        AddFoundButton.IsEnabled = idle && FoundList.SelectedItems.Count > 0;
        ManufacturerCombo.IsEnabled = idle;
        CancelButton.IsEnabled = !idle;
        CatalogList.SelectionChanged -= OnCatalogSelected;
        CatalogList.SelectionChanged += OnCatalogSelected;
    }

    private void OnCatalogSelected(object? sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        UpdateButtons();
    }

    private async Task AddFileAsync()
    {
        if (ManufacturerId is not int mfr) return;
        var files = FilePickers.OpenFiles(this.OwnerWindow(), "Select catalog PDF(s)", FilePickers.PdfFilter, allowMultiple: true);
        if (files.Count == 0) return;
        var ids = files.Select(f => _service.Add(mfr, null, null, f).Id).ToList();
        LoadCatalogs(ids[0]);
        await IndexAsync(ids);
    }

    private async Task AddLinksAsync(IReadOnlyList<string> links)
    {
        if (ManufacturerId is not int mfr) return;
        var good = links.Where(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)).ToList();
        if (good.Count == 0)
        {
            SetStatus("Paste a full link starting with https://", AppColors.Danger);
            return;
        }
        var ids = good.Select(l => _service.Add(mfr, null, l, null).Id).ToList();
        LinkBox.Text = string.Empty;
        LoadCatalogs(ids[0]);
        await IndexAsync(ids);
    }

    /// <summary>Downloads (if needed) and reads the catalogs one after another.</summary>
    private async Task IndexAsync(IReadOnlyList<int> catalogIds)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        UpdateButtons();
        var failures = new List<string>();
        int done = 0;
        try
        {
            IPdfOcrEngine? ocr = null;
#if WINDOWS10_0_19041_0_OR_GREATER
            ocr = new HardwareTemplateBuilder.Shared.WindowsPdfOcrEngine();
#endif
            for (int i = 0; i < catalogIds.Count; i++)
            {
                string prefix = catalogIds.Count > 1 ? $"Catalog {i + 1} of {catalogIds.Count}: " : string.Empty;
                var progress = new Progress<string>(s => { if (!cts.IsCancellationRequested) SetStatus(prefix + s, AppColors.Muted); });
                try
                {
                    await Task.Run(() => _service.IndexAsync(catalogIds[i], ocr, progress, cts.Token));
                    done++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }
            SetStatus(failures.Count == 0
                    ? $"Read {done} catalog(s). They are now used by the template lookups."
                    : $"Read {done} catalog(s); {failures.Count} failed: {string.Join("; ", failures.Distinct())}",
                failures.Count == 0 ? AppColors.Success : AppColors.Danger);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped. Use Read Text Again to finish a catalog later.", AppColors.Muted);
        }
        finally
        {
            _cts = null;
            LoadCatalogs((CatalogList.SelectedItem as CatalogRow)?.Catalog.Id);
        }
    }

    private async Task FindOnlineAsync()
    {
        if (ManufacturerId is not int mfr) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        UpdateButtons();
        FoundList.ItemsSource = null;
        var finder = new TemplateFinderService(Http, DatabaseInitializer.CreateContext);
        var progress = new Progress<string>(s => { if (!cts.IsCancellationRequested) SetStatus(s, AppColors.Muted); });
        var name = ManufacturerName;
        try
        {
            var links = await Task.Run(() => finder.FindCatalogLinksAsync(mfr, name, progress, cts.Token));
            var saved = _service.ForManufacturer(mfr).Select(c => c.OnlineLink).Where(l => l != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = links.Where(l => !saved.Contains(l.Url)).Select(l => new FoundRow(l.Url, l.Score)).ToList();
            FoundList.ItemsSource = rows;
            var blocked = finder.BlockedSites;
            SetStatus((rows.Count > 0
                    ? $"Found {rows.Count} catalog link(s). Select the ones you want and click Add Selected."
                    : "No catalogs found on the website. Try Search Google in Browser and paste the PDF's link.") +
                (blocked.Count > 0 ? $"\n{string.Join(", ", blocked)} blocked the automated search (bot protection); search on Google instead." : string.Empty),
                rows.Count > 0 ? AppColors.Success : AppColors.Muted);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Search stopped.", AppColors.Muted);
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}", AppColors.Danger);
        }
        finally
        {
            _cts = null;
            UpdateButtons();
        }
    }

    private async Task RenameAsync()
    {
        if (CatalogList.SelectedItem is not CatalogRow r) return;
        var owner = this.OwnerWindow();
        var title = await DialogHelper.PromptAsync(owner, "Catalog title:", "Rename Catalog", r.Catalog.Title);
        if (string.IsNullOrWhiteSpace(title)) return;
        _service.Rename(r.Catalog.Id, title);
        LoadCatalogs(r.Catalog.Id);
    }

    private async Task OpenAsync()
    {
        if (CatalogList.SelectedItem is not CatalogRow r) return;
        try
        {
            var path = await Task.Run(() => _service.EnsureLocalFileAsync(r.Catalog.Id, null, CancellationToken.None));
            OpenShell(path);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open the catalog: {ex.Message}", AppColors.Danger);
        }
    }

    private async Task RemoveAsync()
    {
        if (CatalogList.SelectedItem is not CatalogRow r) return;
        var owner = this.OwnerWindow();
        if (!await DialogHelper.ConfirmAsync(owner, $"Remove the catalog \"{r.Catalog.Title}\"?\n\nIts text is removed from the database. A downloaded copy is deleted; your own file is left where it is.", "Remove Catalog"))
            return;
        _service.Remove(r.Catalog.Id);
        LoadCatalogs();
        SetStatus("Catalog removed.", AppColors.Muted);
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }

    private void OpenShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open it: {ex.Message}", AppColors.Danger);
        }
    }
}
