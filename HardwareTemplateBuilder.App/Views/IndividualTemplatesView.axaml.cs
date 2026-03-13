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

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="IndividualTemplate"/> records.</summary>
public partial class IndividualTemplatesView : UserControl
{
    private IndividualTemplateRepository? _repo;
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _descriptions = new();
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
        var context = DatabaseInitializer.CreateContext();
        _repo = new IndividualTemplateRepository(context);
        _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descriptions = new DescriptionRepository(context).GetAll().OrderBy(d => d.DescriptionText).ToList();
        _doorMaterials = new DoorMaterialRepository(context).GetAll().ToList();

        ManufacturerCombo.ItemsSource = _manufacturers;
        ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        DescriptionCombo.ItemsSource = _descriptions;
        DescriptionCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
        DoorMaterialCombo.ItemsSource = _doorMaterials;
        DoorMaterialCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");

        // Filter combo: "(Any)" sentinel + all door materials.
        var anyMaterial = new List<DoorMaterial> { new DoorMaterial { Id = 0, Material = "(Any)" } };
        anyMaterial.AddRange(_doorMaterials);
        DoorMaterialFilterCombo.ItemsSource = anyMaterial;
        DoorMaterialFilterCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");
        DoorMaterialFilterCombo.SelectedIndex = 0;

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        DoorMaterialFilterCombo.SelectionChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
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

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var matFilter = DoorMaterialFilterCombo.SelectedItem as DoorMaterial;
        var matId = matFilter?.Id ?? 0;

        var items = _repo!.GetAll()
            .Where(t => string.IsNullOrEmpty(filter) || t.TemplateNumber.ToLower().Contains(filter))
            .Where(t => matId == 0 || t.DoorMaterialId == matId)
            .OrderBy(t => t.TemplateNumber)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is IndividualTemplate t)
        {
            _selectedId = t.Id;
            ManufacturerCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == t.ManufacturerId);
            DescriptionCombo.SelectedItem = _descriptions.FirstOrDefault(d => d.Id == t.DescriptionId);
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
        if (DescriptionCombo.SelectedItem is not Description desc) { StatusLabel.Text = "Description is required."; return; }
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
            DescriptionId = desc.Id,
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
            var saved = _repo!.Add(entity);
            savedId = saved.Id;
        }
        else
        {
            savedId = _selectedId;
            var existing = _repo!.GetById(_selectedId);
            if (existing != null)
            {
                existing.ManufacturerId = entity.ManufacturerId;
                existing.DescriptionId = entity.DescriptionId;
                existing.TemplateNumber = entity.TemplateNumber;
                existing.NumPages = entity.NumPages;
                existing.PagesToPrint = entity.PagesToPrint;
                existing.PagesToRotate = entity.PagesToRotate;
                existing.RotationDirection = entity.RotationDirection;
                existing.DoorMaterialId = entity.DoorMaterialId;
                existing.OnlineLink = entity.OnlineLink;
                existing.LocalLink = entity.LocalLink;
                _repo.Update(existing);
            }
        }

        LoadList();

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
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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
        _repo!.Delete(_selectedId);
        ClearForm();
        LoadList();
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
        RecordList.SelectedItem = null;
    }
}
