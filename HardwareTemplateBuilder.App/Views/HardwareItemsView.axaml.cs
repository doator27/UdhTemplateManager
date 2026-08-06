using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
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
    private bool _updatingDescCombo;

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

        // Populate Manufacturer combo with "(Any)" sentinel at index 0.
        var anyMfr = new List<Manufacturer> { new() { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        MfrCombo.ItemsSource = anyMfr;
        MfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        MfrCombo.SelectedIndex = 0;

        // Populate Description combo with "(Any)" sentinel at index 0.
        var anyDesc = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        anyDesc.AddRange(_descComboItems);
        DescCombo.ItemsSource = anyDesc;
        DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        DescCombo.SelectedIndex = 0;

        // Wire search controls � manufacturer drives description cascade.
        MfrCombo.SelectionChanged  += (_, _) => OnSearchMfrChanged();
        DescCombo.SelectionChanged += (_, _) => { if (!_updatingDescCombo) LoadList(); };
        FilterBox.TextChanged      += (_, _) => LoadList();
        ShowInactiveCheck.IsCheckedChanged += (_, _) => LoadList();
        
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        
        ManufacturerCombo.ItemsSource = _manufacturers;
        ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

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
        
        // Show all items on first load.
        LoadList();
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

    /// <summary>
    /// Repopulates the Description combo to show only descriptions that have at least one
    /// hardware item made by the selected manufacturer, then re-runs the search.
    /// </summary>
    private void OnSearchMfrChanged()
    {
        var mfr = MfrCombo.SelectedItem as Manufacturer;
        var mfrId = mfr?.Id ?? 0;

        var filtered = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };

        if (mfrId == 0)
        {
            filtered.AddRange(_descComboItems);
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = ctx.HardwareItems
                .Where(h => h.ManufacturerId == mfrId && h.IsActive)
                .Select(h => h.DescriptionId)
                .Distinct()
                .ToHashSet();
            filtered.AddRange(_descComboItems.Where(d => descIds.Contains(d.Id)));
        }

        _updatingDescCombo = true;
        try
        {
            DescCombo.ItemsSource = filtered;
            DescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
            DescCombo.SelectedIndex = 0;
        }
        finally
        {
            _updatingDescCombo = false;
        }

        LoadList();
    }

    /// <summary>
    /// Reloads the search results. When <paramref name="selectItemId"/> is given, the matching
    /// row is selected and scrolled into view afterward — used after Save() so a newly created
    /// or edited item is immediately visible instead of requiring the user to search for it.
    /// </summary>
    private void LoadList(int? selectItemId = null)
    {
        var mfr      = MfrCombo.SelectedItem as Manufacturer;
        var descItem = DescCombo.SelectedItem as DescriptionComboItem;
        var model    = FilterBox.Text?.Trim();
        var showInactive = ShowInactiveCheck.IsChecked == true;

        var mfrName = (mfr      == null || mfr.Id      == 0) ? null : mfr.ManufacturerName;
        var descId  = (descItem == null || descItem.Id == 0) ? (int?)null : descItem.Id;

        var results = _repo!.Search(mfrName, descId, model, activeOnly: !showInactive)
            .Select(h => new HardwareItemDisplay(h))
            .ToList();

        RecordList.ItemsSource = results;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        if (selectItemId.HasValue)
        {
            var match = results.FirstOrDefault(r => r.Item.Id == selectItemId.Value);
            if (match != null)
            {
                RecordList.SelectedItem = match;
                RecordList.ScrollIntoView(match);
                RecordList.Focus();
                return;
            }
        }

        // Default to first item.
        if (results.Count > 0)
            RecordList.SelectedIndex = 0;
        else
            ClearSelectedInfo();
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is HardwareItemDisplay d)
        {
            _selectedId = d.Item.Id;
            var h = d.Item;
            
            // Update selected info panel
            SelectedMfrLabel.Text   = $"Manufacturer: {h.Manufacturer?.ManufacturerName ?? "?"}"; 
            SelectedDescLabel.Text  = $"Description: {h.Description?.DescriptionText ?? "?"}"; 
            SelectedModelLabel.Text = $"Model: {h.ModelNumber}";
            SelectedFreqLabel.Text  = $"Frequency: {h.Frequency}";
            
            // Update form fields
            ManufacturerCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == h.ManufacturerId);
            _selectedDescItem = _descComboItems.FirstOrDefault(desc => desc.Id == h.DescriptionId);
            DescriptionLabel.Text = _selectedDescItem?.DisplayText ?? "(none)";
            ModelNumberBox.Text = h.ModelNumber;
            RemarksBox.Text = h.Remarks ?? "";
            FrequencyBox.Text = h.Frequency.ToString();
            IsActiveCheck.IsChecked = h.IsActive;
            StatusLabel.Text = "";
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

        bool isNew = _selectedId == 0;
        int savedId;

        if (isNew)
        {
            var created = _repo!.Add(new HardwareItem
            {
                ManufacturerId = mfr.Id,
                DescriptionId  = desc.Id,
                ModelNumber    = modelNumber,
                Remarks        = string.IsNullOrEmpty(RemarksBox.Text?.Trim()) ? null : RemarksBox.Text.Trim(),
                IsActive       = IsActiveCheck.IsChecked == true
            });
            savedId = created.Id;
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
            savedId = _selectedId;
        }
        StatusLabel.Text = "Saved.";
        _selectedId = savedId;

        if (isNew)
        {
            // Broaden the search filters so the newly created item is guaranteed to appear,
            // rather than silently staying hidden if it doesn't match whatever the search
            // panel happened to be filtered by.
            MfrCombo.SelectedIndex = 0;
            FilterBox.Text = "";
            if (IsActiveCheck.IsChecked != true)
                ShowInactiveCheck.IsChecked = true;
        }
        LoadList(savedId);
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
            DisplayText = $"{mfr} � {desc} � {item.ModelNumber}";
        }
    }
}
