using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// CRUD view for <see cref="HardwareItem"/> records, including a linked-templates sub-panel
/// for managing <see cref="HardwareItemTemplate"/> junction records.
/// </summary>
public partial class HardwareItemsView : UserControl
{
    private HardwareItemRepository? _repo;
    private HardwareItemTemplateRepository? _hitRepo;
    private IndividualTemplateRepository? _templateRepo;
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _descriptions = new();
    private int _selectedId;
    private bool _updatingSearchDescCombo;

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
        _hitRepo = new HardwareItemTemplateRepository(context);
        _templateRepo = new IndividualTemplateRepository(context);
        _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descriptions = new DescriptionRepository(context).GetAll().OrderBy(d => d.DescriptionText).ToList();

        ManufacturerCombo.ItemsSource = _manufacturers;
        ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        DescriptionCombo.ItemsSource = _descriptions;
        DescriptionCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");

        // Search combos for linked template panel
        var searchMfrs = new List<Manufacturer> { new Manufacturer { Id = 0, ManufacturerName = "(Any)" } };
        searchMfrs.AddRange(_manufacturers);
        SearchMfrCombo.ItemsSource = searchMfrs;
        SearchMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        SearchMfrCombo.SelectedIndex = 0;

        var searchDescs = new List<Description> { new Description { Id = 0, DescriptionText = "(Any)" } };
        searchDescs.AddRange(_descriptions);
        SearchDescCombo.ItemsSource = searchDescs;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
        SearchDescCombo.SelectedIndex = 0;

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this hardware item?"))
                DeleteSelected();
        };

        // Template search
        SearchMfrCombo.SelectionChanged += (_, _) => OnSearchMfrChanged();
        SearchDescCombo.SelectionChanged += (_, _) => { if (!_updatingSearchDescCombo) SearchTemplates(); };
        SearchTemplateNumBox.TextChanged += (_, _) => SearchTemplates();
        LinkButton.Click += (_, _) => LinkTemplate();
        UnlinkButton.Click += (_, _) => UnlinkTemplate();
    }

    /// <summary>
    /// Repopulates the Description search combo to show only descriptions that appear on at
    /// least one template made by the selected manufacturer, then re-runs the template search.
    /// </summary>
    private void OnSearchMfrChanged()
    {
        var mfr = SearchMfrCombo.SelectedItem as Manufacturer;
        var mfrId = mfr?.Id ?? 0;

        var filtered = new List<Description> { new() { Id = 0, DescriptionText = "(Any)" } };

        if (mfrId == 0)
        {
            filtered.AddRange(_descriptions);
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = ctx.IndividualTemplates
                .Where(t => t.ManufacturerId == mfrId)
                .Select(t => t.DescriptionId)
                .Distinct()
                .ToHashSet();
            filtered.AddRange(_descriptions.Where(d => descIds.Contains(d.Id)));
        }

        _updatingSearchDescCombo = true;
        try
        {
            SearchDescCombo.ItemsSource = filtered;
            SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
            SearchDescCombo.SelectedIndex = 0;
        }
        finally
        {
            _updatingSearchDescCombo = false;
        }

        SearchTemplates();
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
            DescriptionCombo.SelectedItem = _descriptions.FirstOrDefault(d => d.Id == h.DescriptionId);
            ModelNumberBox.Text = h.ModelNumber;
            RemarksBox.Text = h.Remarks ?? "";
            FrequencyBox.Text = h.Frequency.ToString();
            StatusLabel.Text = "";
            LoadLinkedTemplates();
        }
    }

    private void LoadLinkedTemplates()
    {
        if (_selectedId == 0) { LinkedTemplatesList.ItemsSource = null; return; }
        var context = DatabaseInitializer.CreateContext();
        var linked = context.HardwareItemTemplates
            .Include(hit => hit.IndividualTemplate)
            .Where(hit => hit.HardwareItemId == _selectedId)
            .Select(hit => hit.IndividualTemplate)
            .OrderBy(t => t.TemplateNumber)
            .ToList();
        LinkedTemplatesList.ItemsSource = linked;
        LinkedTemplatesList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    private void SearchTemplates()
    {
        var mfr = SearchMfrCombo.SelectedItem as Manufacturer;
        var desc = SearchDescCombo.SelectedItem as Description;
        var num = SearchTemplateNumBox.Text?.Trim();

        var mfrName = (mfr == null || mfr.Id == 0) ? null : mfr.ManufacturerName;
        var descText = (desc == null || desc.Id == 0) ? null : desc.DescriptionText;

        var context = DatabaseInitializer.CreateContext();
        var query = context.IndividualTemplates
            .Include(t => t.Manufacturer)
            .Include(t => t.Description)
            .AsQueryable();

        if (!string.IsNullOrEmpty(mfrName))
            query = query.Where(t => t.Manufacturer.ManufacturerName.Contains(mfrName));
        if (!string.IsNullOrEmpty(descText))
            query = query.Where(t => t.Description.DescriptionText.Contains(descText));
        if (!string.IsNullOrEmpty(num))
            query = query.Where(t => t.TemplateNumber.Contains(num));

        var results = query.OrderBy(t => t.TemplateNumber).ToList();
        TemplateSearchList.ItemsSource = results;
        TemplateSearchList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    private void LinkTemplate()
    {
        if (_selectedId == 0) { LinkStatusLabel.Text = "Select a hardware item first."; return; }
        if (TemplateSearchList.SelectedItem is not IndividualTemplate t) { LinkStatusLabel.Text = "Select a template to link."; return; }
        _hitRepo!.Add(new HardwareItemTemplate { HardwareItemId = _selectedId, IndividualTemplateId = t.Id });
        LinkStatusLabel.Text = $"Linked: {t.TemplateNumber}";
        LoadLinkedTemplates();
    }

    private void UnlinkTemplate()
    {
        if (_selectedId == 0) { LinkStatusLabel.Text = "Select a hardware item first."; return; }
        if (LinkedTemplatesList.SelectedItem is not IndividualTemplate t) { LinkStatusLabel.Text = "Select a linked template to unlink."; return; }

        var context = DatabaseInitializer.CreateContext();
        var link = context.HardwareItemTemplates
            .FirstOrDefault(hit => hit.HardwareItemId == _selectedId && hit.IndividualTemplateId == t.Id);
        if (link != null)
        {
            context.HardwareItemTemplates.Remove(link);
            context.SaveChanges();
            LinkStatusLabel.Text = $"Unlinked: {t.TemplateNumber}";
            LoadLinkedTemplates();
        }
    }

    private void Save()
    {
        if (ManufacturerCombo.SelectedItem is not Manufacturer mfr) { StatusLabel.Text = "Manufacturer is required."; return; }
        if (DescriptionCombo.SelectedItem is not Description desc) { StatusLabel.Text = "Description is required."; return; }
        var modelNumber = ModelNumberBox.Text?.Trim();
        if (string.IsNullOrEmpty(modelNumber)) { StatusLabel.Text = "Model Number is required."; return; }

        if (_selectedId == 0)
        {
            _repo!.Add(new HardwareItem
            {
                ManufacturerId = mfr.Id,
                DescriptionId = desc.Id,
                ModelNumber = modelNumber,
                Remarks = string.IsNullOrEmpty(RemarksBox.Text?.Trim()) ? null : RemarksBox.Text.Trim()
            });
        }
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null)
            {
                existing.ManufacturerId = mfr.Id;
                existing.DescriptionId = desc.Id;
                existing.ModelNumber = modelNumber;
                existing.Remarks = string.IsNullOrEmpty(RemarksBox.Text?.Trim()) ? null : RemarksBox.Text.Trim();
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
        LinkedTemplatesList.ItemsSource = null;
        RecordList.SelectedItem = null;
    }
}
