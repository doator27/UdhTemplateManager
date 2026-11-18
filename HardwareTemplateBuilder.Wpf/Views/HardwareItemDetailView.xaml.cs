using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Ui;
using System.Threading.Tasks;

using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using HardwareTemplateBuilder.Core.Services.Catalogs;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>
/// Detail view for a single <see cref="HardwareItem"/>: manages linked templates.
/// The primary addition method is Quick-Add (creates or reuses the template record
/// automatically). A secondary search panel lets users link existing templates by search.
/// Navigated to from <see cref="HardwareItemsView"/>.
/// </summary>
public partial class HardwareItemDetailView : UserControl
{
    private readonly int _itemId;
    private readonly string? _returnRoute;
    private int _itemManufacturerId;
    private int _itemDescriptionId;
    private string _itemModelNumber = string.Empty;
    private string _itemManufacturerName = string.Empty;
    private int _editingTemplateId;
    private List<DescriptionComboItem> _descComboItems = new();
    private List<DoorMaterial> _doorMaterials = new();
    private List<HardwareItem> _importSourceItems = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the detail view for the given hardware item.</summary>
    /// <param name="itemId">The ID of the hardware item to manage.</param>
    /// <param name="returnRoute">
    /// Optional navigation route to return to when Back is clicked, overriding the default
    /// "HardwareItems" list. Used when navigating here from a context (e.g. bulk hardware entry)
    /// that should be restored afterward.
    /// </param>
    public HardwareItemDetailView(int itemId, string? returnRoute = null)
    {
        _itemId = itemId;
        _returnRoute = returnRoute;
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
        _itemModelNumber    = item?.ModelNumber    ?? string.Empty;
        _itemManufacturerName = item?.Manufacturer?.ManufacturerName ?? string.Empty;

        var allRawDescs = new DescriptionRepository(context).GetAll().ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(allRawDescs, activeOnly: true);
        _doorMaterials  = new DoorMaterialRepository(context).GetAll().OrderBy(d => d.Material).ToList();

        // Quick-add combos — pre-filter descriptions to item's description subtree.
        var descendantIds = _itemDescriptionId > 0
            ? GetDescendantIds(_itemDescriptionId, allRawDescs)
            : null;
        var addDescItems = descendantIds != null
            ? _descComboItems.Where(d => descendantIds.Contains(d.Id)).ToList()
            : _descComboItems;
        AddDescCombo.ItemsSource = addDescItems.Count > 0 ? addDescItems : _descComboItems;
        AddDescCombo.DisplayMemberPath = "DisplayText";

        AddDoorMatCombo.ItemsSource = _doorMaterials;
        AddDoorMatCombo.DisplayMemberPath = "Material";

        // Search desc combo — same descendant filter with "(Any)" sentinel.
        var searchDescItems = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        searchDescItems.AddRange(addDescItems.Count > 0 ? addDescItems : _descComboItems);
        SearchDescCombo.ItemsSource = searchDescItems;
        SearchDescCombo.DisplayMemberPath = "DisplayText";
        SearchDescCombo.SelectedIndex = 0;

        LoadLinkedTemplates();
        SearchTemplates();
        LoadImportSourceItems();

        BackButton.Click           += (_, _) => NavigationRequested?.Invoke(_returnRoute ?? "HardwareItems");
        RemoveTemplateButton.Click += (_, _) => RemoveTemplate();
        _pagesHelper = new TemplatePagesHelper(AddOnlineLinkBox, AddLocalLinkBox, AddNumPagesBox, AddPagesToPrintBox);
        ApplyAddDefaults();
        AddTemplateButton.Click    += async (_, _) => await AddLinkTemplateAsync();
        FindOnlineButton.Click     += async (_, _) => await FindTemplateOnlineAsync();
        CatalogLookupButton.Click  += async (_, _) => await LookUpInCatalogAsync();
        RequestedFromManufacturerButton.Click += (_, _) => AddRequestedFromManufacturerLink();
        BrowseLocalLinkButton.Click += async (_, _) => await BrowseLocalLinkAsync();
        SearchDescCombo.SelectionChanged += (_, _) => SearchTemplates();
        SearchTemplateNumBox.TextChanged  += (_, _) => SearchTemplates();
        LinkButton.Click += (_, _) => LinkSearchResult();
        TemplateSearchList.SelectionChanged += async (_, _) =>
            await SearchPreviewPanel.LoadTemplateAsync(TemplateSearchList.SelectedItem as IndividualTemplate);

        LinkedTemplatesList.SelectionChanged += (_, _) => OnLinkedTemplateSelected();
        SaveTemplateChangesButton.Click += async (_, _) => await SaveTemplateChangesAsync();
        CancelEditButton.Click += (_, _) => CancelEdit();

        ImportSourceItemCombo.SelectionChanged += (_, _) => LoadImportCandidateTemplates();
        ImportTemplatesList.SelectionChanged += async (_, _) =>
            await ImportPreviewPanel.LoadTemplateAsync(ImportTemplatesList.SelectedItem as IndividualTemplate);
        ImportSelectedButton.Click += (_, _) => ImportSelectedTemplates();
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
        _pagesHelper?.Reset();

        AddTemplateButton.SetVisible(false);
        SaveTemplateChangesButton.SetVisible(true);
        CancelEditButton.SetVisible(true);
    }

    /// <summary>Resets the Add/Edit panel back to its blank "add a new template" state.</summary>
    private TemplatePagesHelper? _pagesHelper;

    /// <summary>
    /// New-template defaults: the item's own description and door material "Both".
    /// </summary>
    private void ApplyAddDefaults()
    {
        if (AddDescCombo.ItemsSource is System.Collections.IEnumerable items)
            AddDescCombo.SelectedItem = items.OfType<DescriptionComboItem>().FirstOrDefault(d => d.Id == _itemDescriptionId);
        AddDoorMatCombo.SelectedItem = TemplatePageInfo.DefaultMaterial(_doorMaterials);
    }

    private void ResetAddForm()
    {
        _editingTemplateId = 0;
        AddPanelHeader.Text          = "Add Template";
        AddTemplateNumBox.Text       = "";
        AddPagesToPrintBox.Text      = "";
        AddNumPagesBox.Text          = "";
        AddPagesToRotateBox.Text     = "";
        AddRotationDirectionBox.Text = "0";
        AddOnlineLinkBox.Text        = "";
        AddLocalLinkBox.Text         = "";
        ApplyAddDefaults();
        _pagesHelper?.Reset();

        AddTemplateButton.SetVisible(true);
        SaveTemplateChangesButton.SetVisible(false);
        CancelEditButton.SetVisible(false);
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
    private async Task SaveTemplateChangesAsync()
    {
        if (_editingTemplateId == 0) return;
        if (AddDescCombo.SelectedItem is not DescriptionComboItem desc) { AddStatusLabel.Text = "Description is required."; return; }
        if (AddDoorMatCombo.SelectedItem is not DoorMaterial mat)  { AddStatusLabel.Text = "Door Material is required."; return; }

        var templateNum = AddTemplateNumBox.Text?.Trim();
        if (string.IsNullOrEmpty(templateNum)) { AddStatusLabel.Text = "Template # is required."; return; }

        // Blank Pages To Print = all pages of the PDF.
        var pagesToPrint = _pagesHelper != null ? await _pagesHelper.EnsurePagesAsync() : AddPagesToPrintBox.Text?.Trim();
        if (string.IsNullOrEmpty(pagesToPrint)) { AddStatusLabel.Text = "Couldn't read the PDF to count its pages. Enter Pages To Print (e.g. 1-3)."; return; }
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
        LinkedTemplatesList.DisplayMemberPath = "TemplateNumber";
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
    /// <summary>
    /// Opens "Find Template Online" for this item and fills the template form with the chosen PDF
    /// (link, page count, a suggested template number) for the user to check and save.
    /// </summary>
    private async Task FindTemplateOnlineAsync()
    {
        var window = this.OwnerWindow();
        if (window == null) return;
        var found = await TemplateFinderFlow.FindForItemAsync(window, _itemId);
        if (found == null) return;

        AddOnlineLinkBox.Text = found.Url;
        if (found.PageCount > 0) _pagesHelper?.ApplyCount(found.PageCount);
        if (string.IsNullOrWhiteSpace(AddTemplateNumBox.Text)) AddTemplateNumBox.Text = GuessTemplateNumber(found);
        AddStatusLabel.Foreground = AppColors.Success;
        AddStatusLabel.Text = "Link filled in from Find Online. Check the template details, then add or save the template.";
    }

    /// <summary>
    /// Opens the product catalog lookup for this item's model number. A template already in the
    /// database is linked straight away; otherwise the template number (and the description that
    /// fits the catalog wording) are filled into the template form for the user to finish.
    /// </summary>
    private async Task LookUpInCatalogAsync()
    {
        var window = this.OwnerWindow();
        if (window == null || _itemManufacturerId == 0) return;
        var choice = await new CatalogLookupWindow(_itemManufacturerId, _itemManufacturerName, _itemModelNumber, allowChoose: true)
            .ShowDialogAsync<CatalogLookupChoice>(window);
        if (choice == null) return;

        if (choice.TemplateId is int templateId)
        {
            using var context = DatabaseInitializer.CreateContext();
            var link  = new HardwareItemTemplate { HardwareItemId = _itemId, IndividualTemplateId = templateId };
            var saved = new HardwareItemTemplateRepository(context).Add(link);
            AddStatusLabel.Foreground = AppColors.Success;
            AddStatusLabel.Text = ReferenceEquals(saved, link)
                ? $"Linked template {choice.TemplateNumber} (from the product catalog)."
                : $"Template {choice.TemplateNumber} is already linked.";
            LoadLinkedTemplates();
            return;
        }

        if (!string.IsNullOrWhiteSpace(choice.TemplateNumber)) AddTemplateNumBox.Text = choice.TemplateNumber;
        if (choice.DescriptionId is int descId && AddDescCombo.ItemsSource is System.Collections.IEnumerable items &&
            items.OfType<DescriptionComboItem>().FirstOrDefault(d => d.Id == descId) is { } desc)
            AddDescCombo.SelectedItem = desc;
        AddStatusLabel.Foreground = AppColors.Success;
        AddStatusLabel.Text = "Template # filled in from the product catalog" +
            (choice.Description != null ? $" ({choice.Description})" : string.Empty) +
            ". Add the template's link or file (Find Online... can search for it), then add the template.";
    }

    /// <summary>A template number to suggest for a found PDF: the search term it mentions, else its file name.</summary>
    private static string GuessTemplateNumber(TemplateCandidate found)
    {
        var name = found.MatchedTerm ?? System.IO.Path.GetFileNameWithoutExtension(found.FileName);
        return name.Length > 50 ? name[..50] : name;
    }

    private async Task AddLinkTemplateAsync()
    {
        if (_itemManufacturerId == 0)                              { AddStatusLabel.Text = "Hardware item has no manufacturer."; return; }
        if (AddDescCombo.SelectedItem is not DescriptionComboItem desc) { AddStatusLabel.Text = "Description is required."; return; }
        if (AddDoorMatCombo.SelectedItem is not DoorMaterial mat)  { AddStatusLabel.Text = "Door Material is required."; return; }

        var templateNum = AddTemplateNumBox.Text?.Trim();
        if (string.IsNullOrEmpty(templateNum)) { AddStatusLabel.Text = "Template # is required."; return; }

        // Blank Pages To Print = all pages of the PDF.
        var pagesToPrint = _pagesHelper != null ? await _pagesHelper.EnsurePagesAsync() : AddPagesToPrintBox.Text?.Trim();
        if (string.IsNullOrEmpty(pagesToPrint)) { AddStatusLabel.Text = "Couldn't read the PDF to count its pages. Enter Pages To Print (e.g. 1-3)."; return; }

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
        AddPagesToPrintBox.Text       = "";
        AddNumPagesBox.Text           = "";
        AddPagesToRotateBox.Text      = "";
        AddRotationDirectionBox.Text  = "0";
        AddOnlineLinkBox.Text         = "";
        AddLocalLinkBox.Text          = "";

        LoadLinkedTemplates();
    }

    /// <summary>
    /// Marks this hardware item as having a template requested from the manufacturer but not yet
    /// received. Links the hardware item directly to the specific placeholder
    /// <see cref="IndividualTemplate"/> configured in App Settings
    /// ("RequestedFromManufacturerTemplateId"), setting
    /// <see cref="HardwareItemTemplate.IsRequestedFromManufacturer"/> to true.
    /// </summary>
    private void AddRequestedFromManufacturerLink()
    {
        using var context = DatabaseInitializer.CreateContext();
        var hitRepo = new HardwareItemTemplateRepository(context);

        var templateIdText = new AppSettingRepository(context).GetValue("RequestedFromManufacturerTemplateId");
        if (!int.TryParse(templateIdText, out var templateId))
        {
            AddStatusLabel.Text = "No placeholder template configured. Set one under App Settings first.";
            return;
        }

        var template = context.IndividualTemplates.FirstOrDefault(t => t.Id == templateId);
        if (template == null)
        {
            AddStatusLabel.Text = "The configured placeholder template no longer exists. Choose a new one under App Settings.";
            return;
        }

        var existingLink = context.HardwareItemTemplates
            .FirstOrDefault(hit => hit.HardwareItemId == _itemId && hit.IndividualTemplateId == template.Id && hit.JobId == null);
        if (existingLink != null)
        {
            existingLink.IsRequestedFromManufacturer = true;
            context.SaveChanges();
        }
        else
        {
            var link = new HardwareItemTemplate
            {
                HardwareItemId              = _itemId,
                IndividualTemplateId        = template.Id,
                IsRequestedFromManufacturer = true,
            };
            hitRepo.Add(link);
        }

        AddStatusLabel.Text = $"Marked as requested from manufacturer: {template.TemplateNumber}";
        LoadLinkedTemplates();
    }

    /// <summary>Opens a file picker and populates the Local File path box.</summary>
    private async Task BrowseLocalLinkAsync()
    {
        await System.Threading.Tasks.Task.CompletedTask;
        var files = FilePickers.OpenFiles(this.OwnerWindow(), "Select PDF File", FilePickers.PdfFilter);

        if (files.Count > 0)
            AddLocalLinkBox.Text = files[0];
            if (_pagesHelper != null) await _pagesHelper.DetectAsync(force: true);
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
        TemplateSearchList.DisplayMemberPath = "TemplateNumber";
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

    // --- Import templates from another hardware item ---

    /// <summary>
    /// Populates <see cref="ImportSourceItemCombo"/> with other active hardware items that share
    /// this item's manufacturer and description, excluding this item itself.
    /// </summary>
    private void LoadImportSourceItems()
    {
        using var context = DatabaseInitializer.CreateContext();
        _importSourceItems = context.HardwareItems
            .Where(h => h.Id != _itemId
                && h.IsActive
                && h.ManufacturerId == _itemManufacturerId
                && h.DescriptionId == _itemDescriptionId)
            .OrderBy(h => h.ModelNumber)
            .ToList();

        ImportSourceItemCombo.ItemsSource = _importSourceItems;
        ImportSourceItemCombo.DisplayMemberPath = "ModelNumber";
        ImportSourceItemCombo.SelectedItem = null;
        ImportTemplatesList.ItemsSource = null;
        ImportStatusLabel.Text = _importSourceItems.Count == 0
            ? "No other hardware items with the same manufacturer and description were found."
            : string.Empty;
    }

    /// <summary>Loads the templates linked to the selected import source item.</summary>
    private void LoadImportCandidateTemplates()
    {
        _ = ImportPreviewPanel.LoadTemplateAsync(null);

        if (ImportSourceItemCombo.SelectedItem is not HardwareItem sourceItem)
        {
            ImportTemplatesList.ItemsSource = null;
            return;
        }

        using var context = DatabaseInitializer.CreateContext();
        var templates = context.HardwareItemTemplates
            .Include(hit => hit.IndividualTemplate)
            .Where(hit => hit.HardwareItemId == sourceItem.Id)
            .Select(hit => hit.IndividualTemplate)
            .OrderBy(t => t.TemplateNumber)
            .ToList();

        ImportTemplatesList.ItemsSource = templates;
        ImportTemplatesList.DisplayMemberPath = "TemplateNumber";
    }

    /// <summary>
    /// Clones each selected template into a brand-new, independent <see cref="IndividualTemplate"/>
    /// row (bypassing the repository's dedup-by-Manufacturer+Number+DoorMaterial check, which
    /// would otherwise just hand back the source item's own row) and links the clones to this
    /// hardware item, so subsequent edits never affect the source item's templates.
    /// </summary>
    private void ImportSelectedTemplates()
    {
        var selected = ImportTemplatesList.SelectedItems?.OfType<IndividualTemplate>().ToList();
        if (selected == null || selected.Count == 0)
        {
            ImportStatusLabel.Text = "Select at least one template to import.";
            return;
        }

        using var context = DatabaseInitializer.CreateContext();
        var hitRepo = new HardwareItemTemplateRepository(context);
        var importedCount = 0;

        foreach (var source in selected)
        {
            var clone = new IndividualTemplate
            {
                ManufacturerId    = source.ManufacturerId,
                DescriptionId     = source.DescriptionId,
                DoorMaterialId    = source.DoorMaterialId,
                TemplateNumber    = source.TemplateNumber,
                PagesToPrint      = source.PagesToPrint,
                NumPages          = source.NumPages,
                PagesToRotate     = source.PagesToRotate,
                RotationDirection = source.RotationDirection,
                OnlineLink        = source.OnlineLink,
                LocalLink         = source.LocalLink,
            };
            context.IndividualTemplates.Add(clone);
            context.SaveChanges();

            hitRepo.Add(new HardwareItemTemplate { HardwareItemId = _itemId, IndividualTemplateId = clone.Id });
            importedCount++;
        }

        ImportStatusLabel.Text = $"Imported {importedCount} template(s) as independent copies.";
        LoadLinkedTemplates();
    }
}
