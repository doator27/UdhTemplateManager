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
    private HardwareItemRepository? _repo;
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _descComboItems = new();
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public HardwareItemsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new HardwareItemRepository(context);
        _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(new DescriptionRepository(context).GetAll());

        ManufacturerCombo.ItemsSource = _manufacturers;
        ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        DescriptionCombo.ItemsSource = _descComboItems;
        DescriptionCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        OpenItemButton.Click += (_, _) => OpenItem();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this hardware item?"))
                DeleteSelected();
        };
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(h => string.IsNullOrEmpty(filter) || h.ModelNumber.ToLower().Contains(filter))
            .OrderBy(h => h.ModelNumber)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is HardwareItem h)
        {
            _selectedId = h.Id;
            ManufacturerCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == h.ManufacturerId);
            DescriptionCombo.SelectedItem = _descComboItems.FirstOrDefault(d => d.Id == h.DescriptionId);
            ModelNumberBox.Text = h.ModelNumber;
            RemarksBox.Text = h.Remarks ?? "";
            FrequencyBox.Text = h.Frequency.ToString();
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
        if (DescriptionCombo.SelectedItem is not DescriptionComboItem desc) { StatusLabel.Text = "Description is required."; return; }
        var modelNumber = ModelNumberBox.Text?.Trim();
        if (string.IsNullOrEmpty(modelNumber)) { StatusLabel.Text = "Model Number is required."; return; }

        if (_selectedId == 0)
        {
            _repo!.Add(new HardwareItem
            {
                ManufacturerId = mfr.Id,
                DescriptionId  = desc.Id,
                ModelNumber    = modelNumber,
                Remarks        = string.IsNullOrEmpty(RemarksBox.Text?.Trim()) ? null : RemarksBox.Text.Trim()
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
        DescriptionCombo.SelectedItem = null;
        ModelNumberBox.Text = "";
        RemarksBox.Text = "";
        FrequencyBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
