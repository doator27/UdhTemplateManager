using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    private int _itemManufacturerId;
    private int _itemDescriptionId;
    private int _editingTemplateId;
    private List<DescriptionComboItem> _descComboItems = new();
    private List<DoorMaterial> _doorMaterials = new();

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

        _itemManufacturerId = item?.ManufacturerId ?? 0;
        _itemDescriptionId  = item?.DescriptionId  ?? 0;

        var allRawDescs = new DescriptionRepository(context).GetAll().ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(allRawDescs);
        _doorMaterials  = new DoorMaterialRepository(context).GetAll().OrderBy(d => d.Material).ToList();

        // Quick-add combos — pre-filter descriptions to item's description subtree.
        var descendantIds = _itemDescriptionId > 0
            ? GetDescendantIds(_itemDescriptionId, allRawDescs)
            : null;
        var addDescItems = descendantIds != null
            ? _descComboItems.Where(d => descendantIds.Contains(d.Id)).ToList()
            : _descComboItems;
        AddDescCombo.ItemsSource = addDescItems.Count > 0 ? addDescItems : _descComboItems;
        AddDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        AddDoorMatCombo.ItemsSource = _doorMaterials;
        AddDoorMatCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");

        // Search desc combo — same descendant filter with "(Any)" sentinel.
        var searchDescItems = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        searchDescItems.AddRange(addDescItems.Count > 0 ? addDescItems : _descComboItems);
        SearchDescCombo.ItemsSource = searchDescItems;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        SearchDescCombo.SelectedIndex = 0;

        LoadLinkedTemplates();
        SearchTemplates();

        BackButton.Click           += (_, _) => NavigationRequested?.Invoke("HardwareItems");
        RemoveTemplateButton.Click += (_, _) => RemoveTemplate();
        AddTemplateButton.Click    += (_, _) => AddLinkTemplate();
        BrowseLocalLinkButton.Click += async (_, _) => await BrowseLocalLinkAsync();
        SearchDescCombo.SelectionChanged += (_, _) => SearchTemplates();
        SearchTemplateNumBox.TextChanged  += (_, _) => SearchTemplates();
        LinkButton.Click += (_, _) => LinkSearchResult();

        LinkedTemplatesList.SelectionChanged += (_, _) => OnLinkedTemplateSelected();
        SaveTemplateChangesButton.Click += (_, _) => SaveTemplateChanges();
        CancelEditButton.Click += (_, _) => CancelEdit();
    }

    /// <summary>
    /// Populates the Add/Edit panel from the selected linked template so its fields can be
    /// edited directly, switching the panel from "add" mode into "edit" mode.
    /// </summary>
    private void OnLinkedTemplateSelected()
    {
        if (LinkedTemplatesList.SelectedItem is not IndividualTemplate t)
            return;

        _editingTemplateId = t.Id;
        AddPanelHeader.Text = $"Edit Linked Template: {t.TemplateNumber}";
        AddTemplateNumBox.Text       = t.TemplateNumber;
        AddDescCombo.SelectedItem    = _descComboItems.FirstOrDefault(d => d.Id == t.DescriptionId);
        AddDoorMatCombo.SelectedItem = _doorMaterials.FirstOrDefault(dm => dm.Id == t.DoorMaterialId);
        AddNumPagesBox.Text          = t.NumPages.ToString();
        AddPagesToPrintBox.Text      = t.PagesToPrint;
        AddPagesToRotateBox.Text     = t.PagesToRotate ?? "";
        AddRotationDirectionBox.Text = t.RotationDirection.ToString();
        AddOnlineLinkBox.Text        = t.OnlineLink ?? "";
        AddLocalLinkBox.Text         = t.LocalLink ?? "";
        AddStatusLabel.Text          = "";

        AddTemplateButton.IsVisible         = false;
        SaveTemplateChangesButton.IsVisible = true;
        CancelEditButton.IsVisible          = true;
    }

    /// <summary>Resets the Add/Edit panel back to its blank "add a new template" state.</summary>
    private void ResetAddForm()
    {
        _editingTemplateId = 0;
        AddPanelHeader.Text          = "Add Template";
        AddTemplateNumBox.Text       = "";
        AddPagesToPrintBox.Text      = "1";
        AddNumPagesBox.Text          = "1";
        AddPagesToRotateBox.Text     = "";
        AddRotationDirectionBox.Text = "0";
        AddOnlineLinkBox.Text        = "";
        AddLocalLinkBox.Text         = "";
        AddDescCombo.SelectedItem    = null;
        AddDoorMatCombo.SelectedItem = null;

        AddTemplateButton.IsVisible         = true;
        SaveTemplateChangesButton.IsVisible = false;
        CancelEditButton.IsVisible          = false;
        LinkedTemplatesList.SelectedItem    = null;
    }

    private void CancelEdit()
    {
        ResetAddForm();
        AddStatusLabel.Text = "";
    }

    /// <summary>
    /// Saves in-place edits to the template currently selected in the Linked Templates list
    /// (an update to the existing <see cref="IndividualTemplate"/> row — not a new record).
    /// </summary>
    private void SaveTemplateChanges()
    {
        if (_editingTemplateId == 0) return;
        if (AddDescCombo.SelectedItem is not DescriptionComboItem desc) { AddStatusLabel.Text = "Description is required."; return; }
        if (AddDoorMatCombo.SelectedItem is not DoorMaterial mat)  { AddStatusLabel.Text = "Door Material is required."; return; }

        var templateNum = AddTemplateNumBox.Text?.Trim();
        if (string.IsNullOrEmpty(templateNum)) { AddStatusLabel.Text = "Template # is required."; return; }

        var pagesToPrint = AddPagesToPrintBox.Text?.Trim();
        if (string.IsNullOrEmpty(pagesToPrint)) pagesToPrint = "1";
        try { new PageRangeParser().Parse(pagesToPrint); }
        catch { AddStatusLabel.Text = "Pages To Print format invalid. Use e.g. 1,3-5,8."; return; }

        if (!int.TryParse(AddNumPagesBox.Text?.Trim(), out var numPages) || numPages < 1)
        { AddStatusLabel.Text = "Num Pages must be a positive integer."; return; }

        var pagesToRotate = AddPagesToRotateBox.Text?.Trim();
        if (!string.IsNullOrEmpty(pagesToRotate))
        {
            try { new PageRangeParser().Parse(pagesToRotate); }
            catch { AddStatusLabel.Text = "Pages To Rotate format invalid."; return; }
        }

        if (!int.TryParse(AddRotationDirectionBox.Text?.Trim() ?? "0", out var rotationDirection))
        { AddStatusLabel.Text = "Rotation Direction must be an integer (e.g. 90 or -90)."; return; }

        var onlineLink = AddOnlineLinkBox.Text?.Trim();
        var localLink  = AddLocalLinkBox.Text?.Trim();
        if (string.IsNullOrEmpty(onlineLink) && string.IsNullOrEmpty(localLink))
        { AddStatusLabel.Text = "Provide at least an Online Link or a Local File path."; return; }

        using var context = DatabaseInitializer.CreateContext();
        var existing = context.IndividualTemplates.Find(_editingTemplateId);
        if (existing == null) { AddStatusLabel.Text = "That template no longer exists."; return; }

        existing.DescriptionId     = desc.Id;
        existing.DoorMaterialId    = mat.Id;
        existing.TemplateNumber    = templateNum;
        existing.PagesToPrint      = pagesToPrint;
        existing.NumPages          = numPages;
        existing.PagesToRotate     = string.IsNullOrEmpty(pagesToRotate) ? null : pagesToRotate;
        existing.RotationDirection = rotationDirection;
        existing.OnlineLink        = string.IsNullOrEmpty(onlineLink) ? null : onlineLink;
        existing.LocalLink         = string.IsNullOrEmpty(localLink)  ? null : localLink;
        context.SaveChanges();

        var savedNumber = existing.TemplateNumber;
        LoadLinkedTemplates();
        ResetAddForm();
        AddStatusLabel.Text = $"Saved changes to: {savedNumber}";
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
            if (t.Id == _editingTemplateId) ResetAddForm();
        }
    }

    /// <summary>
    /// Primary add path: finds or creates the template from the Quick-Add fields, then links
    /// it to this hardware item. Fully deduplicates — both the template record and the link.
    /// Manufacturer is always inherited from the parent hardware item.
    /// </summary>
    private void AddLinkTemplate()
    {
        if (_itemManufacturerId == 0)                              { AddStatusLabel.Text = "Hardware item has no manufacturer."; return; }
        if (AddDescCombo.SelectedItem is not DescriptionComboItem desc) { AddStatusLabel.Text = "Description is required."; return; }
        if (AddDoorMatCombo.SelectedItem is not DoorMaterial mat)  { AddStatusLabel.Text = "Door Material is required."; return; }

        var templateNum = AddTemplateNumBox.Text?.Trim();
        if (string.IsNullOrEmpty(templateNum)) { AddStatusLabel.Text = "Template # is required."; return; }

        var pagesToPrint = AddPagesToPrintBox.Text?.Trim();
        if (string.IsNullOrEmpty(pagesToPrint)) pagesToPrint = "1";

        if (!int.TryParse(AddNumPagesBox.Text?.Trim(), out var numPages) || numPages < 1)
        { AddStatusLabel.Text = "Num Pages must be a positive integer."; return; }

        var pagesToRotate = AddPagesToRotateBox.Text?.Trim();
        if (!string.IsNullOrEmpty(pagesToRotate))
        {
            try { new PageRangeParser().Parse(pagesToRotate); }
            catch { AddStatusLabel.Text = "Pages To Rotate format invalid. Use e.g. 1,3-5,8."; return; }
        }

        if (!int.TryParse(AddRotationDirectionBox.Text?.Trim() ?? "0", out var rotationDirection))
        { AddStatusLabel.Text = "Rotation Direction must be an integer (e.g. 90 or -90)."; return; }

        var onlineLink = AddOnlineLinkBox.Text?.Trim();
        var localLink  = AddLocalLinkBox.Text?.Trim();

        if (string.IsNullOrEmpty(onlineLink) && string.IsNullOrEmpty(localLink))
        {
            AddStatusLabel.Text = "Provide at least an Online Link or a Local File path.";
            return;
        }

        using var context = DatabaseInitializer.CreateContext();
        var templateRepo = new IndividualTemplateRepository(context);
        var hitRepo      = new HardwareItemTemplateRepository(context);

        var candidate = new IndividualTemplate
        {
            ManufacturerId    = _itemManufacturerId,
            DescriptionId     = desc.Id,
            DoorMaterialId    = mat.Id,
            TemplateNumber    = templateNum,
            PagesToPrint      = pagesToPrint,
            NumPages          = numPages,
            PagesToRotate     = string.IsNullOrEmpty(pagesToRotate) ? null : pagesToRotate,
            RotationDirection = rotationDirection,
            OnlineLink        = string.IsNullOrEmpty(onlineLink) ? null : onlineLink,
            LocalLink         = string.IsNullOrEmpty(localLink)  ? null : localLink,
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
        AddTemplateNumBox.Text        = "";
        AddPagesToPrintBox.Text       = "1";
        AddNumPagesBox.Text           = "1";
        AddPagesToRotateBox.Text      = "";
        AddRotationDirectionBox.Text  = "0";
        AddOnlineLinkBox.Text         = "";
        AddLocalLinkBox.Text          = "";

        LoadLinkedTemplates();
    }

    /// <summary>Opens a file picker and populates the Local File path box.</summary>
    private async Task BrowseLocalLinkAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select PDF File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } }
            }
        });

        if (files.Count > 0)
            AddLocalLinkBox.Text = files[0].Path.LocalPath;
    }

    // --- Search and link (secondary) ---

    /// <summary>
    /// Searches for templates belonging to the same manufacturer as this hardware item,
    /// optionally filtered by description subtree and template number substring.
    /// </summary>
    private void SearchTemplates()
    {
        var descItem = SearchDescCombo.SelectedItem as DescriptionComboItem;
        var num      = SearchTemplateNumBox.Text?.Trim();
        var descId   = (descItem == null || descItem.Id == 0) ? (int?)null : descItem.Id;

        using var context = DatabaseInitializer.CreateContext();
        var query = context.IndividualTemplates
            .Include(t => t.Manufacturer)
            .Include(t => t.Description)
            .AsQueryable();

        // Always restrict to the same manufacturer as the hardware item.
        if (_itemManufacturerId > 0)
            query = query.Where(t => t.ManufacturerId == _itemManufacturerId);

        if (descId.HasValue)            query = query.Where(t => t.DescriptionId == descId.Value);
        if (!string.IsNullOrEmpty(num)) query = query.Where(t => t.TemplateNumber.Contains(num));

        var results = query.OrderBy(t => t.TemplateNumber).ToList();
        TemplateSearchList.ItemsSource = results;
        TemplateSearchList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
    }

    /// <summary>
    /// Returns the ID of <paramref name="rootId"/> plus all of its recursive descendants.
    /// </summary>
    private static HashSet<int> GetDescendantIds(int rootId, IEnumerable<Description> allDescs)
    {
        var lookup = allDescs.ToLookup(d => d.ParentId);
        var result = new HashSet<int>();
        var queue  = new Queue<int>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            result.Add(id);
            foreach (var child in lookup[(int?)id])
                queue.Enqueue(child.Id);
        }
        return result;
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
