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
/// Detail view for a single <see cref="HardwareItem"/>: manages linked templates.
/// The primary addition method is Quick-Add (creates or reuses the template record
/// automatically). A secondary search panel lets users link existing templates by search.
/// Navigated to from <see cref="HardwareItemsView"/>.
/// </summary>
public partial class HardwareItemDetailView : UserControl
{
    private readonly int _itemId;
    private List<Manufacturer> _manufacturers = new();
    private List<DescriptionComboItem> _descComboItems = new();
    private List<DoorMaterial> _doorMaterials = new();
    private bool _updatingSearchDescCombo;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the detail view for the given hardware item.</summary>
    /// <param name="itemId">The ID of the hardware item to manage.</param>
    public HardwareItemDetailView(int itemId)
    {
        _itemId = itemId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var context = DatabaseInitializer.CreateContext();

        var item = context.HardwareItems
            .Include(h => h.Manufacturer)
            .Include(h => h.Description)
            .FirstOrDefault(h => h.Id == _itemId);
        ItemTitleLabel.Text = item != null
            ? $"{item.Manufacturer?.ManufacturerName} — {item.ModelNumber}"
            : $"Item #{_itemId}";

        _manufacturers  = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(new DescriptionRepository(context).GetAll());
        _doorMaterials  = new DoorMaterialRepository(context).GetAll().OrderBy(d => d.Material).ToList();

        // Quick-add combos
        AddMfrCombo.ItemsSource = _manufacturers;
        AddMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        AddDescCombo.ItemsSource = _descComboItems;
        AddDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        AddDoorMatCombo.ItemsSource = _doorMaterials;
        AddDoorMatCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");

        // Search combos
        var anyMfr = new List<Manufacturer> { new() { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        SearchMfrCombo.ItemsSource = anyMfr;
        SearchMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        SearchMfrCombo.SelectedIndex = 0;

        var anyDesc = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        anyDesc.AddRange(_descComboItems);
        SearchDescCombo.ItemsSource = anyDesc;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        SearchDescCombo.SelectedIndex = 0;

        LoadLinkedTemplates();

        MainMenuButton.Click    += (_, _) => NavigationRequested?.Invoke("Dashboard");
        BackButton.Click        += (_, _) => NavigationRequested?.Invoke("HardwareItems");
        RemoveTemplateButton.Click += (_, _) => RemoveTemplate();
        AddTemplateButton.Click    += (_, _) => AddLinkTemplate();
        SearchMfrCombo.SelectionChanged  += (_, _) => OnSearchMfrChanged();
        SearchDescCombo.SelectionChanged += (_, _) => { if (!_updatingSearchDescCombo) SearchTemplates(); };
        SearchTemplateNumBox.TextChanged  += (_, _) => SearchTemplates();
        LinkButton.Click += (_, _) => LinkSearchResult();
    }

    private void LoadLinkedTemplates()
    {
        using var context = DatabaseInitializer.CreateContext();
        var linked = context.HardwareItemTemplates
            .Include(hit => hit.IndividualTemplate)
                .ThenInclude(t => t.Manufacturer)
            .Where(hit => hit.HardwareItemId == _itemId)
            .Select(hit => hit.IndividualTemplate)
            .OrderBy(t => t.TemplateNumber)
            .ToList();
        LinkedTemplatesList.ItemsSource = linked;
        LinkedTemplatesList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    private void RemoveTemplate()
    {
        if (LinkedTemplatesList.SelectedItem is not IndividualTemplate t)
        {
            RemoveStatusLabel.Text = "Select a linked template to remove.";
            return;
        }
        using var context = DatabaseInitializer.CreateContext();
        var link = context.HardwareItemTemplates
            .FirstOrDefault(hit => hit.HardwareItemId == _itemId && hit.IndividualTemplateId == t.Id);
        if (link != null)
        {
            context.HardwareItemTemplates.Remove(link);
            context.SaveChanges();
            RemoveStatusLabel.Text = $"Removed: {t.TemplateNumber}";
            LoadLinkedTemplates();
        }
    }

    /// <summary>
    /// Primary add path: finds or creates the template from the Quick-Add fields, then links
    /// it to this hardware item. Fully deduplicates — both the template record and the link.
    /// </summary>
    private void AddLinkTemplate()
    {
        if (AddMfrCombo.SelectedItem is not Manufacturer mfr)    { AddStatusLabel.Text = "Manufacturer is required."; return; }
        if (AddDescCombo.SelectedItem is not DescriptionComboItem desc) { AddStatusLabel.Text = "Description is required."; return; }
        if (AddDoorMatCombo.SelectedItem is not DoorMaterial mat) { AddStatusLabel.Text = "Door Material is required."; return; }

        var templateNum = AddTemplateNumBox.Text?.Trim();
        if (string.IsNullOrEmpty(templateNum)) { AddStatusLabel.Text = "Template # is required."; return; }

        var pagesToPrint = AddPagesToPrintBox.Text?.Trim();
        if (string.IsNullOrEmpty(pagesToPrint)) pagesToPrint = "1";

        var onlineLink = AddOnlineLinkBox.Text?.Trim();

        using var context = DatabaseInitializer.CreateContext();
        var templateRepo = new IndividualTemplateRepository(context);
        var hitRepo      = new HardwareItemTemplateRepository(context);

        var candidate = new IndividualTemplate
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            DoorMaterialId = mat.Id,
            TemplateNumber = templateNum,
            PagesToPrint   = pagesToPrint,
            NumPages       = 1,
            OnlineLink     = string.IsNullOrEmpty(onlineLink) ? null : onlineLink,
        };

        // Add() returns existing record if (Manufacturer, TemplateNumber, DoorMaterial) match.
        var saved      = templateRepo.Add(candidate);
        bool isExisting = !ReferenceEquals(saved, candidate);

        // Link() is also idempotent via FindDuplicate on HardwareItemTemplateRepository.
        var link      = new HardwareItemTemplate { HardwareItemId = _itemId, IndividualTemplateId = saved.Id };
        var savedLink = hitRepo.Add(link);
        bool alreadyLinked = !ReferenceEquals(savedLink, link);

        if (alreadyLinked)
            AddStatusLabel.Text = $"Already linked: {saved.TemplateNumber}";
        else if (isExisting)
            AddStatusLabel.Text = $"Linked existing: {saved.TemplateNumber}";
        else
            AddStatusLabel.Text = $"Created and linked: {saved.TemplateNumber}";

        // Clear fields for next entry.
        AddTemplateNumBox.Text  = "";
        AddPagesToPrintBox.Text = "1";
        AddOnlineLinkBox.Text   = "";

        LoadLinkedTemplates();
    }

    // --- Search and link (secondary) ---

    private void OnSearchMfrChanged()
    {
        var mfr   = SearchMfrCombo.SelectedItem as Manufacturer;
        var mfrId = mfr?.Id ?? 0;

        var filtered = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        if (mfrId == 0)
        {
            filtered.AddRange(_descComboItems);
        }
        else
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var descIds = ctx.IndividualTemplates
                .Where(t => t.ManufacturerId == mfrId)
                .Select(t => t.DescriptionId)
                .Distinct()
                .ToHashSet();
            filtered.AddRange(_descComboItems.Where(d => descIds.Contains(d.Id)));
        }

        _updatingSearchDescCombo = true;
        try
        {
            SearchDescCombo.ItemsSource = filtered;
            SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
            SearchDescCombo.SelectedIndex = 0;
        }
        finally { _updatingSearchDescCombo = false; }

        SearchTemplates();
    }

    private void SearchTemplates()
    {
        var mfr      = SearchMfrCombo.SelectedItem as Manufacturer;
        var descItem = SearchDescCombo.SelectedItem as DescriptionComboItem;
        var num      = SearchTemplateNumBox.Text?.Trim();
        var mfrName  = (mfr      == null || mfr.Id      == 0) ? null : mfr.ManufacturerName;
        var descId   = (descItem == null || descItem.Id == 0) ? (int?)null : descItem.Id;

        using var context = DatabaseInitializer.CreateContext();
        var query = context.IndividualTemplates
            .Include(t => t.Manufacturer)
            .Include(t => t.Description)
            .AsQueryable();

        if (!string.IsNullOrEmpty(mfrName)) query = query.Where(t => t.Manufacturer.ManufacturerName.Contains(mfrName));
        if (descId.HasValue)                query = query.Where(t => t.DescriptionId == descId.Value);
        if (!string.IsNullOrEmpty(num))     query = query.Where(t => t.TemplateNumber.Contains(num));

        var results = query.OrderBy(t => t.TemplateNumber).ToList();
        TemplateSearchList.ItemsSource = results;
        TemplateSearchList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    private void LinkSearchResult()
    {
        if (TemplateSearchList.SelectedItem is not IndividualTemplate t)
        {
            LinkStatusLabel.Text = "Select a template to link.";
            return;
        }

        using var context = DatabaseInitializer.CreateContext();
        var hitRepo = new HardwareItemTemplateRepository(context);
        var link    = new HardwareItemTemplate { HardwareItemId = _itemId, IndividualTemplateId = t.Id };
        var saved   = hitRepo.Add(link);

        LinkStatusLabel.Text = ReferenceEquals(saved, link)
            ? $"Linked: {t.TemplateNumber}"
            : $"Already linked: {t.TemplateNumber}";

        LoadLinkedTemplates();
    }
}
