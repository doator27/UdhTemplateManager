using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// List and CRUD view for <see cref="HardwareItem"/> records.
/// Select an item and click <c>Open Item</c> to manage its linked templates.
/// </summary>
public partial class HardwareItemsView : UserControl
{
    private AppDbContext? _context;
    private HardwareItemRepository? _repo;
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _descComboItems = new();
    private List<Description> _allRawDescs = new();
    private DescriptionComboItem? _selectedDescItem;
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public HardwareItemsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
        Unloaded += (_, _) => Cleanup();
    }

    private void Initialize()
    {
        _context = DatabaseInitializer.CreateContext();
        _repo = new HardwareItemRepository(_context);
        _manufacturers = new ManufacturerRepository(_context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _allRawDescs   = new DescriptionRepository(_context).GetAll().ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(_allRawDescs);

        ManufacturerCombo.ItemsSource = _manufacturers;
        ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        ShowInactiveCheck.IsCheckedChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        OpenItemButton.Click += (_, _) => OpenItem();
        PickDescriptionButton.Click += async (_, _) => await PickDescriptionAsync();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this hardware item?"))
                DeleteSelected();
        };
    }

    private async System.Threading.Tasks.Task PickDescriptionAsync()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        var selectedId = await new DescriptionPickerWindow(_allRawDescs).ShowDialog<int?>(window);
        if (selectedId == null) return;
        _selectedDescItem = _descComboItems.FirstOrDefault(d => d.Id == selectedId.Value);
        DescriptionLabel.Text = _selectedDescItem?.DisplayText ?? "(none)";
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var showInactive = ShowInactiveCheck.IsChecked == true;
        
        var items = _repo!.GetAll()
            .Where(h => showInactive || h.IsActive)
            .Where(h => string.IsNullOrEmpty(filter) || h.ModelNumber.ToLower().Contains(filter))
            .OrderBy(h => h.ModelNumber)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayModelNumber");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is HardwareItem h)
        {
            _selectedId = h.Id;
            ManufacturerCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == h.ManufacturerId);
            _selectedDescItem = _descComboItems.FirstOrDefault(d => d.Id == h.DescriptionId);
            DescriptionLabel.Text = _selectedDescItem?.DisplayText ?? "(none)";
            ModelNumberBox.Text = h.ModelNumber;
            RemarksBox.Text = h.Remarks ?? "";
            FrequencyBox.Text = h.Frequency.ToString();
            IsActiveCheck.IsChecked = h.IsActive;
            StatusLabel.Text = "";
        }
    }

    private void OpenItem()
    {
        if (_selectedId == 0) { StatusLabel.Text = "Select a hardware item to open."; return; }
        NavigationRequested?.Invoke($"HardwareItemDetail:{_selectedId}");
    }

    private void Save()
    {
        if (ManufacturerCombo.SelectedItem is not Manufacturer mfr) { StatusLabel.Text = "Manufacturer is required."; return; }
        if (_selectedDescItem == null) { StatusLabel.Text = "Description is required."; return; }
        var desc = _selectedDescItem;
        var modelNumber = ModelNumberBox.Text?.Trim();
        if (string.IsNullOrEmpty(modelNumber)) { StatusLabel.Text = "Model Number is required."; return; }

        if (_selectedId == 0)
        {
            _repo!.Add(new HardwareItem
            {
                ManufacturerId = mfr.Id,
                DescriptionId  = desc.Id,
                ModelNumber    = modelNumber,
                Remarks        = string.IsNullOrEmpty(RemarksBox.Text?.Trim()) ? null : RemarksBox.Text.Trim(),
                IsActive       = IsActiveCheck.IsChecked == true
            });
        }
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null)
            {
                existing.ManufacturerId = mfr.Id;
                existing.DescriptionId  = desc.Id;
                existing.ModelNumber    = modelNumber;
                existing.Remarks        = string.IsNullOrEmpty(RemarksBox.Text?.Trim()) ? null : RemarksBox.Text.Trim();
                existing.IsActive       = IsActiveCheck.IsChecked == true;
                _repo.Update(existing);
            }
        }
        StatusLabel.Text = "Saved.";
        LoadList();
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
        _selectedDescItem = null;
        DescriptionLabel.Text = "(none)";
        ModelNumberBox.Text = "";
        RemarksBox.Text = "";
        FrequencyBox.Text = "";
        IsActiveCheck.IsChecked = true;
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }

    private void Cleanup()
    {
        _context?.Dispose();
        _context = null;
    }
}
