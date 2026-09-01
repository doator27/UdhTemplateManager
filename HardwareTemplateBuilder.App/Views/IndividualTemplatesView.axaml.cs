using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="IndividualTemplate"/> records.</summary>
public partial class IndividualTemplatesView : UserControl
{
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _descComboItems = new();
    private List<DescriptionComboItem> _leafDescComboItems = new();
    private List<DoorMaterial> _doorMaterials = new();
    private readonly PageRangeParser _pageRangeParser = new();
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public IndividualTemplatesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using (var context = DatabaseInitializer.CreateContext())
        {
            var allDescriptions = new DescriptionRepository(context).GetAll();

            _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
            // Full tree for the add/edit form — an existing template may already reference a
            // non-leaf description, and the form needs to be able to display/keep that as-is.
            _descComboItems = DescriptionHelper.BuildComboItems(allDescriptions);
            // Leaf-only for the search filter.
            _leafDescComboItems = DescriptionHelper.BuildLeafComboItems(allDescriptions);
            _doorMaterials = new DoorMaterialRepository(context).GetAll().ToList();
        }

        ManufacturerCombo.ItemsSource = _manufacturers;
        ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        DescriptionCombo.ItemsSource = _descComboItems;
        DescriptionCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        DoorMaterialCombo.ItemsSource = _doorMaterials;
        DoorMaterialCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");

        MfrList.ItemsSource = _manufacturers;
        MfrList.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        MfrList.SelectionChanged += (_, _) => { RefreshDescList(); RefreshTemplateNumberList(); };
        DescList.SelectionChanged += (_, _) => RefreshTemplateNumberList();
        TemplateNumberList.SelectionChanged += (_, _) => OnSelectionChanged();
        TemplateNumberSearchBox.TextChanged += (_, _) => RefreshTemplateNumberList();

        RefreshDescList();
        RefreshTemplateNumberList();

        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        BrowseLocalLinkButton.Click += async (_, _) => await BrowseLocalLink();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this template?"))
                DeleteSelected();
        };
    }

    /// <summary>
    /// Repopulates the search Description list to show only leaf descriptions that have at
    /// least one template under any of the currently selected manufacturers (all leaf
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
            var descIds = ctx.IndividualTemplates
                .Where(t => mfrIds.Contains(t.ManufacturerId))
                .Select(t => t.DescriptionId)
                .Distinct()
                .ToHashSet();
            items = _leafDescComboItems.Where(d => descIds.Contains(d.Id)).ToList();
        }

        DescList.ItemsSource = items;
        DescList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
    }

    /// <summary>
    /// Repopulates the Template Number list with every template matching the currently selected
    /// Manufacturers and Descriptions — a match is any template whose manufacturer is among the
    /// selected manufacturers (if any are selected) AND whose description is among the selected
    /// descriptions (if any are selected). With nothing selected in either list, every template
    /// is shown, narrowing only as selections are made.
    /// </summary>
    private void RefreshTemplateNumberList()
    {
        var mfrIds  = SelectedIds(MfrList, (Manufacturer m) => m.Id);
        var descIds = SelectedIds(DescList, (DescriptionComboItem d) => d.Id);

        using var ctx = DatabaseInitializer.CreateContext();
        var query = ctx.IndividualTemplates.AsQueryable();
        if (mfrIds.Count > 0) query = query.Where(t => mfrIds.Contains(t.ManufacturerId));
        if (descIds.Count > 0) query = query.Where(t => descIds.Contains(t.DescriptionId));

        var searchText = TemplateNumberSearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(searchText))
            query = query.Where(t => t.TemplateNumber.Contains(searchText));

        var results = query.OrderBy(t => t.TemplateNumber).ToList();
        TemplateNumberList.ItemsSource = results;
        TemplateNumberList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    /// <summary>Returns the IDs of the currently selected items in a multi-select ListBox.</summary>
    private static HashSet<int> SelectedIds<T>(ListBox listBox, Func<T, int> idSelector) =>
        listBox.SelectedItems?.Cast<T>().Select(idSelector).ToHashSet() ?? new HashSet<int>();

    private async System.Threading.Tasks.Task BrowseLocalLink()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Template PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } } }
        });
        if (files.Count > 0)
            LocalLinkBox.Text = files[0].Path.LocalPath;
    }

    private void OnSelectionChanged()
    {
        if (TemplateNumberList.SelectedItem is IndividualTemplate t)
        {
            _selectedId = t.Id;
            ManufacturerCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == t.ManufacturerId);
            DescriptionCombo.SelectedItem = _descComboItems.FirstOrDefault(dc => dc.Id == t.DescriptionId);
            TemplateNumberBox.Text = t.TemplateNumber;
            NumPagesBox.Text = t.NumPages.ToString();
            PagesToPrintBox.Text = t.PagesToPrint;
            PagesToRotateBox.Text = t.PagesToRotate ?? "";
            RotationDirectionBox.Text = t.RotationDirection.ToString();
            DoorMaterialCombo.SelectedItem = _doorMaterials.FirstOrDefault(dm => dm.Id == t.DoorMaterialId);
            OnlineLinkBox.Text = t.OnlineLink ?? "";
            LocalLinkBox.Text = t.LocalLink ?? "";
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        if (ManufacturerCombo.SelectedItem is not Manufacturer mfr) { StatusLabel.Text = "Manufacturer is required."; return; }
        if (DescriptionCombo.SelectedItem is not DescriptionComboItem desc) { StatusLabel.Text = "Description is required."; return; }
        if (DoorMaterialCombo.SelectedItem is not DoorMaterial dm) { StatusLabel.Text = "Door Material is required."; return; }

        var templateNumber = TemplateNumberBox.Text?.Trim();
        if (string.IsNullOrEmpty(templateNumber)) { StatusLabel.Text = "Template Number is required."; return; }

        if (!int.TryParse(NumPagesBox.Text?.Trim(), out var numPages) || numPages < 1)
        { StatusLabel.Text = "Num Pages must be a positive integer."; return; }

        var pagesToPrint = PagesToPrintBox.Text?.Trim();
        if (string.IsNullOrEmpty(pagesToPrint)) { StatusLabel.Text = "Pages To Print is required."; return; }
        try { _pageRangeParser.Parse(pagesToPrint); }
        catch { StatusLabel.Text = "Pages To Print format invalid. Use e.g. 1,3-5,8."; return; }

        var pagesToRotate = PagesToRotateBox.Text?.Trim();
        if (!string.IsNullOrEmpty(pagesToRotate))
        {
            try { _pageRangeParser.Parse(pagesToRotate); }
            catch { StatusLabel.Text = "Pages To Rotate format invalid."; return; }
        }

        if (!int.TryParse(RotationDirectionBox.Text?.Trim() ?? "0", out var rotation))
        { StatusLabel.Text = "Rotation Direction must be an integer (e.g. 90 or -90)."; return; }

        var entity = new IndividualTemplate
        {
            ManufacturerId = mfr.Id,
            DescriptionId = desc.Id, // DescriptionComboItem.Id
            TemplateNumber = templateNumber,
            NumPages = numPages,
            PagesToPrint = pagesToPrint,
            PagesToRotate = string.IsNullOrEmpty(pagesToRotate) ? null : pagesToRotate,
            RotationDirection = rotation,
            DoorMaterialId = dm.Id,
            OnlineLink = string.IsNullOrEmpty(OnlineLinkBox.Text?.Trim()) ? null : OnlineLinkBox.Text.Trim(),
            LocalLink = string.IsNullOrEmpty(LocalLinkBox.Text?.Trim()) ? null : LocalLinkBox.Text.Trim()
        };

        int savedId;
        if (_selectedId == 0)
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var saved = new IndividualTemplateRepository(ctx).Add(entity);
            savedId = saved.Id;
            _selectedId = savedId;
        }
        else
        {
            savedId = _selectedId;
            using var ctx = DatabaseInitializer.CreateContext();
            var existing = ctx.IndividualTemplates.Find(_selectedId);
            if (existing != null)
            {
                existing.ManufacturerId    = entity.ManufacturerId;
                existing.DescriptionId     = entity.DescriptionId;
                existing.TemplateNumber    = entity.TemplateNumber;
                existing.NumPages          = entity.NumPages;
                existing.PagesToPrint      = entity.PagesToPrint;
                existing.PagesToRotate     = entity.PagesToRotate;
                existing.RotationDirection = entity.RotationDirection;
                existing.DoorMaterialId    = entity.DoorMaterialId;
                existing.OnlineLink        = entity.OnlineLink;
                existing.LocalLink         = entity.LocalLink;
                ctx.SaveChanges();
            }
        }

        RefreshTemplateNumberList();

        if (!string.IsNullOrWhiteSpace(entity.OnlineLink))
        {
            StatusLabel.Text = "Saved. Downloading template file...";
            _ = DownloadTemplateAsync(savedId);
        }
        else
        {
            StatusLabel.Text = "Saved.";
        }
    }

    /// <summary>
    /// Downloads a single template's PDF in the background and updates the status label.
    /// Reads the save location from AppSettings.
    /// </summary>
    private async Task DownloadTemplateAsync(int templateId)
    {
        try
        {
            string saveLocation;
            using (var ctx = DatabaseInitializer.CreateContext())
            {
                var settings = new AppSettingRepository(ctx);
                var configured = settings.GetValue("TemplateStorageLocation");
                saveLocation = !string.IsNullOrWhiteSpace(configured)
                    ? configured
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            using var refreshCtx = DatabaseInitializer.CreateContext();
            var handler = new System.Net.Http.SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            var service = new TemplateRefreshService(refreshCtx, http);
            var success = await service.RefreshSingleAsync(templateId, saveLocation);

            Dispatcher.UIThread.Post(() =>
                StatusLabel.Text = success ? "Saved. Template downloaded." : "Saved.");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                StatusLabel.Text = $"Saved. Download failed: {ex.Message}");
        }
    }

    private void DeleteSelected()
    {
        if (_selectedId == 0) return;
        using var ctx = DatabaseInitializer.CreateContext();
        new IndividualTemplateRepository(ctx).Delete(_selectedId);
        ClearForm();
        RefreshTemplateNumberList();
    }

    private void ClearForm()
    {
        _selectedId = 0;
        ManufacturerCombo.SelectedItem = null;
        DescriptionCombo.SelectedItem = null;
        TemplateNumberBox.Text = "";
        NumPagesBox.Text = "";
        PagesToPrintBox.Text = "";
        PagesToRotateBox.Text = "";
        RotationDirectionBox.Text = "0";
        DoorMaterialCombo.SelectedItem = null;
        OnlineLinkBox.Text = "";
        LocalLinkBox.Text = "";
        StatusLabel.Text = "";
        TemplateNumberList.SelectedItem = null;
    }
}
