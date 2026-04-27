using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Step-through wizard that resolves templates for each row produced by
/// <see cref="BulkHardwareEntryView"/>. Reads from <see cref="BulkAddSession"/>.
/// On Finish it persists all <see cref="JobHardware"/> records and navigates to the job detail.
/// </summary>
public partial class TemplateResolutionWizardView : UserControl
{
    private readonly int _jobId;
    private readonly List<BulkHardwareRow> _rows;
    private int _currentIndex;

    private List<Description> _allDescriptions = new();
    private List<DescriptionComboItem> _descComboItems = new();
    private List<DoorMaterial> _doorMaterials = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the wizard for the given job using rows from <see cref="BulkAddSession"/>.</summary>
    /// <param name="jobId">The job to link hardware to on Finish.</param>
    public TemplateResolutionWizardView(int jobId)
    {
        _jobId = jobId;
        _rows  = BulkAddSession.PendingRows;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _allDescriptions = new DescriptionRepository(ctx).GetAll().ToList();
        _descComboItems  = DescriptionHelper.BuildComboItems(_allDescriptions);
        _doorMaterials   = new DoorMaterialRepository(ctx).GetAll().OrderBy(d => d.Material).ToList();

        TplMaterialCombo.ItemsSource = _doorMaterials;
        TplMaterialCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Material");
        if (_doorMaterials.Count > 0) TplMaterialCombo.SelectedIndex = 0;

        PrevButton.Click   += (_, _) => Navigate(-1);
        NextButton.Click   += (_, _) => Navigate(+1);
        FinishButton.Click += (_, _) => OnFinish();

        AddTemplateButton.Click             += (_, _) => AddPendingTemplate();
        RemovePendingTemplateButton.Click   += (_, _) => RemovePendingTemplate();
        RemoveImportedTemplateButton.Click  += (_, _) => RemoveImportedTemplate();
        TplBrowseButton.Click               += async (_, _) => await BrowseLocalFileAsync();

        ToggleSearchButton.Click      += (_, _) => ToggleSearchPanel();
        StageSearchResultButton.Click += (_, _) => StageSearchResult();
        SearchTplNumBox.TextChanged   += (_, _) => RunTemplateSearch();
        SearchTplDescCombo.SelectionChanged += (_, _) => RunTemplateSearch();

        ShowItem(0);
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private void Navigate(int delta)
    {
        var next = _currentIndex + delta;
        if (next < 0 || next >= _rows.Count) return;
        ShowItem(next);
    }

    private void ShowItem(int index)
    {
        _currentIndex = index;
        var row = _rows[index];

        var mfrName = row.SelectedManufacturer?.ManufacturerName ?? "?";
        var model   = row.ModelNumber;
        ItemHeaderLabel.Text = $"Item {index + 1} of {_rows.Count}  —  {mfrName}  {model}";

        // Populate read-only labels display.
        LabelsDisplayPanel.Children.Clear();
        if (row.Labels.Count > 0)
        {
            NoLabelsHint.IsVisible = false;
            foreach (var lbl in row.Labels)
            {
                var line = string.IsNullOrWhiteSpace(lbl.CustomLabel) ? "(blank label)" : lbl.CustomLabel;
                if (!string.IsNullOrWhiteSpace(lbl.Remarks)) line += $"  —  {lbl.Remarks}";
                LabelsDisplayPanel.Children.Add(new TextBlock
                {
                    Text      = $"• {line}",
                    FontSize  = 11,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            }
        }
        else
        {
            NoLabelsHint.IsVisible = true;
        }

        PrevButton.IsEnabled  = index > 0;
        NextButton.IsVisible  = index < _rows.Count - 1;
        FinishButton.IsVisible = index == _rows.Count - 1;

        if (row.MatchedItem != null)
        {
            ShowMatchedCase(row);
        }
        else
        {
            ShowNewCase(row);
        }
    }

    private void ShowMatchedCase(BulkHardwareRow row)
    {
        MatchedPanel.IsVisible = true;
        NewItemPanel.IsVisible = false;
        ItemStatusLabel.Text   = "Matched existing item. Templates below will be linked to this job.";

        using var ctx = DatabaseInitializer.CreateContext();
        var linked = ctx.HardwareItemTemplates
            .Include(hit => hit.IndividualTemplate)
            .Where(hit => hit.HardwareItemId == row.MatchedItem!.Id &&
                          (hit.JobId == null || hit.JobId == _jobId))
            .Select(hit => hit.IndividualTemplate)
            .OrderBy(t => t.TemplateNumber)
            .ToList();
        LinkedTemplatesList.ItemsSource = linked;
        LinkedTemplatesList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");

        // Imported job-specific templates — only shown when coming from ImportHardwareAsync.
        ImportedTemplatesSection.IsVisible = row.PendingTemplates.Count > 0;
        ImportedTemplatesList.ItemsSource  = null;
        ImportedTemplatesList.ItemsSource  = row.PendingTemplates;
        ImportedTemplatesList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
    }

    private void RemoveImportedTemplate()
    {
        var row = _rows[_currentIndex];
        if (ImportedTemplatesList.SelectedItem is BulkPendingTemplate p)
        {
            row.PendingTemplates.Remove(p);
            ImportedTemplatesSection.IsVisible = row.PendingTemplates.Count > 0;
            ImportedTemplatesList.ItemsSource  = null;
            ImportedTemplatesList.ItemsSource  = row.PendingTemplates;
        }
    }

    private void ShowNewCase(BulkHardwareRow row)
    {
        MatchedPanel.IsVisible = false;
        NewItemPanel.IsVisible = true;
        ItemStatusLabel.Text   = "No existing match — this will create a new hardware item.";

        // Pre-filter description combo to the item's subtree
        var descId = row.SelectedDescription?.Id ?? 0;
        List<DescriptionComboItem> descItems;
        if (descId > 0)
        {
            var descendants = GetDescendantIds(descId, _allDescriptions);
            descItems = _descComboItems.Where(d => descendants.Contains(d.Id)).ToList();
        }
        else
        {
            descItems = _descComboItems;
        }
        TplDescCombo.ItemsSource = descItems.Count > 0 ? descItems : _descComboItems;
        TplDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        if (TplDescCombo.ItemCount > 0) TplDescCombo.SelectedIndex = 0;

        // Populate the search description combo with the same subtree filter + an "(Any)" sentinel.
        var searchDescItems = new List<DescriptionComboItem> { new() { Id = 0, DisplayText = "(Any)" } };
        searchDescItems.AddRange(descItems.Count > 0 ? descItems : _descComboItems);
        SearchTplDescCombo.ItemsSource = searchDescItems;
        SearchTplDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        SearchTplDescCombo.SelectedIndex = 0;

        // Reset the search panel state for this item.
        SearchExistingPanel.IsVisible = false;
        ToggleSearchButton.Content    = "Search for existing template…";
        SearchTplNumBox.Text          = "";
        SearchTplResultsList.ItemsSource = null;
        SearchTplStatusLabel.Text        = "";

        RefreshPendingList(row);
    }

    // ── Template staging (Case B) ────────────────────────────────────────────

    private void AddPendingTemplate()
    {
        AddTemplateStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
        var row = _rows[_currentIndex];

        var desc     = TplDescCombo.SelectedItem as DescriptionComboItem;
        var material = TplMaterialCombo.SelectedItem as DoorMaterial;
        var number   = TplNumberBox.Text?.Trim();
        var pages    = TplPagesBox.Text?.Trim();
        var online   = TplOnlineLinkBox.Text?.Trim();
        var local    = TplLocalLinkBox.Text?.Trim();
        var pagesToRotate = TplPagesToRotateBox.Text?.Trim();
        var isJobSpecific = TplJobSpecificCheck.IsChecked == true;

        int numPages = int.TryParse(TplNumPagesBox.Text?.Trim(), out var np) && np > 0 ? np : 1;
        int rotDir   = int.TryParse(TplRotationDirectionBox.Text?.Trim(), out var rd) ? rd : 0;

        if (desc == null)                              { AddTemplateStatusLabel.Text = "Select a description."; return; }
        if (material == null)                          { AddTemplateStatusLabel.Text = "Select a door material."; return; }
        if (string.IsNullOrEmpty(number))              { AddTemplateStatusLabel.Text = "Enter a template number."; return; }
        if (string.IsNullOrEmpty(pages)) pages = "1";
        if (string.IsNullOrEmpty(online) && string.IsNullOrEmpty(local))
        { AddTemplateStatusLabel.Text = "Provide at least an online link or a local file path."; return; }

        var mfrId = row.SelectedManufacturer?.Id ?? 0;
        if (mfrId == 0) { AddTemplateStatusLabel.Text = "Row has no manufacturer."; return; }

        using var ctx = DatabaseInitializer.CreateContext();
        var templateRepo = new IndividualTemplateRepository(ctx);

        // Check if a duplicate already exists before calling Add so we know if this is a new record.
        bool isDedupHit = ctx.IndividualTemplates.Any(t =>
            t.ManufacturerId == mfrId &&
            t.TemplateNumber == number &&
            t.DoorMaterialId == material.Id);

        var candidate = new IndividualTemplate
        {
            ManufacturerId    = mfrId,
            DescriptionId     = desc.Id,
            DoorMaterialId    = material.Id,
            TemplateNumber    = number,
            PagesToPrint      = pages,
            NumPages          = numPages,
            PagesToRotate     = string.IsNullOrEmpty(pagesToRotate) ? null : pagesToRotate,
            RotationDirection = rotDir,
            OnlineLink        = string.IsNullOrEmpty(online) ? null : online,
            LocalLink         = string.IsNullOrEmpty(local)  ? null : local,
        };
        var saved = templateRepo.Add(candidate);

        // Only mark OriginJobId on truly new (not deduped) job-specific templates.
        if (isJobSpecific && !isDedupHit && saved.OriginJobId == null)
        {
            saved.OriginJobId = _jobId;
            ctx.SaveChanges();
        }

        if (!row.PendingTemplates.Any(p => p.Template.Id == saved.Id))
            row.PendingTemplates.Add(new BulkPendingTemplate { Template = saved, IsJobSpecific = isJobSpecific });

        RefreshPendingList(row);
        AddTemplateStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
        AddTemplateStatusLabel.Text = $"Staged: {saved.TemplateNumber}";

        TplNumberBox.Text              = "";
        TplNumPagesBox.Text            = "1";
        TplPagesBox.Text               = "";
        TplPagesToRotateBox.Text       = "";
        TplRotationDirectionBox.Text   = "";
        TplOnlineLinkBox.Text          = "";
        TplLocalLinkBox.Text           = "";
        TplJobSpecificCheck.IsChecked  = false;
    }

    private void RemovePendingTemplate()
    {
        var row = _rows[_currentIndex];
        if (PendingTemplatesList.SelectedItem is BulkPendingTemplate p)
        {
            row.PendingTemplates.Remove(p);
            RefreshPendingList(row);
        }
    }

    private void RefreshPendingList(BulkHardwareRow row)
    {
        PendingTemplatesList.ItemsSource = null;
        PendingTemplatesList.ItemsSource = row.PendingTemplates;
        PendingTemplatesList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
    }

    // ── Finish ───────────────────────────────────────────────────────────────

    private async void OnFinish()
    {
        var skipped = new List<string>();

        using var ctx = DatabaseInitializer.CreateContext();
        var hwRepo      = new HardwareItemRepository(ctx);
        var hitRepo     = new HardwareItemTemplateRepository(ctx);
        var jhRepo      = new JobHardwareRepository(ctx);
        var freqService = new FrequencyService(ctx);

        foreach (var row in _rows)
        {
            int hwItemId;

            if (row.MatchedItem != null)
            {
                // Case A — matched; increment frequency and persist any remark the user set.
                hwItemId = row.MatchedItem.Id;
                var existing = ctx.HardwareItems.Find(hwItemId);
                if (existing != null)
                {
                    freqService.IncrementFrequency(existing);
                    if (!string.IsNullOrWhiteSpace(row.HardwareItemRemarks) &&
                        existing.Remarks != row.HardwareItemRemarks)
                    {
                        existing.Remarks = row.HardwareItemRemarks;
                        ctx.SaveChanges();
                    }
                }

                // Link any job-specific templates carried over from an import.
                foreach (var pending in row.PendingTemplates)
                {
                    int templateId;

                    // If the template was scoped to a different job, clone it for this job.
                    if (pending.Template.OriginJobId.HasValue && pending.Template.OriginJobId != _jobId)
                    {
                        var existingClone = ctx.IndividualTemplates.FirstOrDefault(t =>
                            t.ManufacturerId == pending.Template.ManufacturerId &&
                            t.TemplateNumber == pending.Template.TemplateNumber &&
                            t.DoorMaterialId == pending.Template.DoorMaterialId &&
                            t.OriginJobId    == _jobId);

                        if (existingClone != null)
                        {
                            templateId = existingClone.Id;
                        }
                        else
                        {
                            var clone = new IndividualTemplate
                            {
                                ManufacturerId    = pending.Template.ManufacturerId,
                                DescriptionId     = pending.Template.DescriptionId,
                                DoorMaterialId    = pending.Template.DoorMaterialId,
                                TemplateNumber    = pending.Template.TemplateNumber,
                                NumPages          = pending.Template.NumPages,
                                PagesToPrint      = pending.Template.PagesToPrint,
                                PagesToRotate     = pending.Template.PagesToRotate,
                                RotationDirection = pending.Template.RotationDirection,
                                OnlineLink        = pending.Template.OnlineLink,
                                LocalLink         = pending.Template.LocalLink,
                                OriginJobId       = _jobId
                            };
                            ctx.IndividualTemplates.Add(clone);
                            ctx.SaveChanges();
                            templateId = clone.Id;
                        }
                    }
                    else
                    {
                        templateId = pending.Template.Id;
                    }

                    bool linkExists = ctx.HardwareItemTemplates.Any(hit =>
                        hit.HardwareItemId       == hwItemId &&
                        hit.IndividualTemplateId == templateId &&
                        hit.JobId                == _jobId);

                    if (!linkExists)
                        hitRepo.Add(new HardwareItemTemplate
                        {
                            HardwareItemId       = hwItemId,
                            IndividualTemplateId = templateId,
                            JobId                = _jobId
                        });
                }
            }
            else
            {
                // Case B — create new HardwareItem if not yet created.
                // hwRepo.Add() returns an existing record when a duplicate is found,
                // so explicitly update Remarks afterward to ensure the user's value is saved.
                if (row.CreatedItem == null)
                {
                    var newItem = hwRepo.Add(new HardwareItem
                    {
                        ManufacturerId = row.SelectedManufacturer!.Id,
                        DescriptionId  = row.SelectedDescription!.Id,
                        ModelNumber    = row.ModelNumber,
                        Remarks        = row.HardwareItemRemarks
                    });

                    if (!string.IsNullOrWhiteSpace(row.HardwareItemRemarks) &&
                        newItem.Remarks != row.HardwareItemRemarks)
                    {
                        var tracked = ctx.HardwareItems.Find(newItem.Id);
                        if (tracked != null)
                        {
                            tracked.Remarks = row.HardwareItemRemarks;
                            ctx.SaveChanges();
                        }
                    }

                    row.CreatedItem = newItem;
                }
                hwItemId = row.CreatedItem.Id;

                // Link staged templates to the new hardware item
                foreach (var pending in row.PendingTemplates)
                {
                    hitRepo.Add(new HardwareItemTemplate
                    {
                        HardwareItemId       = hwItemId,
                        IndividualTemplateId = pending.Template.Id,
                        JobId                = pending.IsJobSpecific ? _jobId : null
                    });
                }
            }

            // Create one JobHardware record per label. Skip any that already exist.
            var labelsToSave = row.Labels.Count > 0
                ? row.Labels
                : new System.Collections.Generic.List<BulkHardwareLabel>
                    { new() { CustomLabel = "", Remarks = null } };

            foreach (var lbl in labelsToSave)
            {
                var customLabel = string.IsNullOrWhiteSpace(lbl.CustomLabel) ? null : lbl.CustomLabel;
                var remarks     = string.IsNullOrWhiteSpace(lbl.Remarks)     ? null : lbl.Remarks;

                bool isDuplicate = ctx.JobHardware.Any(jh =>
                    jh.JobId             == _jobId &&
                    jh.HardwareItemId    == hwItemId &&
                    jh.CustomDescription == customLabel);

                if (isDuplicate)
                {
                    skipped.Add(row.ModelNumber + (customLabel != null ? $" ({customLabel})" : string.Empty));
                    continue;
                }

                jhRepo.Add(new JobHardware
                {
                    JobId             = _jobId,
                    HardwareItemId    = hwItemId,
                    CustomDescription = customLabel,
                    Remarks           = remarks
                });
            }
        }

        if (skipped.Count > 0)
        {
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
                await DialogHelper.ShowInfoAsync(win,
                    "The following items were already in the job and were skipped:\n" +
                    string.Join("\n", skipped.Select(s => $"  \u2022 {s}")),
                    "Duplicate Items Skipped");
        }

        BulkAddSession.PendingRows = new();
        NavigationRequested?.Invoke($"JobDetail:{_jobId}");
    }

    // ── Search existing templates (Case B) ──────────────────────────────────

    private void ToggleSearchPanel()
    {
        var visible = !SearchExistingPanel.IsVisible;
        SearchExistingPanel.IsVisible = visible;
        ToggleSearchButton.Content    = visible ? "Hide search ▲" : "Search for existing template…";
        if (visible) RunTemplateSearch();
    }

    /// <summary>
    /// Queries <see cref="IndividualTemplate"/> records filtered to the current row's
    /// manufacturer, optionally by description and a partial template number.
    /// </summary>
    private void RunTemplateSearch()
    {
        if (!SearchExistingPanel.IsVisible) return;

        var row    = _rows[_currentIndex];
        var mfrId  = row.SelectedManufacturer?.Id ?? 0;
        var num    = SearchTplNumBox.Text?.Trim();
        var descItem = SearchTplDescCombo.SelectedItem as DescriptionComboItem;
        var descId   = (descItem == null || descItem.Id == 0) ? (int?)null : descItem.Id;

        using var ctx = DatabaseInitializer.CreateContext();
        var templateRepo = new IndividualTemplateRepository(ctx);
        var query = templateRepo.GetVisibleForJob(_jobId)
            .Include(t => t.Manufacturer)
            .Include(t => t.Description);

        IQueryable<IndividualTemplate> filtered = query;
        if (mfrId > 0)
            filtered = filtered.Where(t => t.ManufacturerId == mfrId);

        if (descId.HasValue)
            filtered = filtered.Where(t => t.DescriptionId == descId.Value);

        if (!string.IsNullOrEmpty(num))
            filtered = filtered.Where(t => t.TemplateNumber.Contains(num));

        var results = filtered.OrderBy(t => t.TemplateNumber).ToList();
        SearchTplResultsList.ItemsSource = results;
        SearchTplResultsList.DisplayMemberBinding = new Avalonia.Data.Binding("TemplateNumber");
        SearchTplStatusLabel.Text = "";
    }

    /// <summary>
    /// Stages the template selected in the search results list onto the current row's
    /// <see cref="BulkHardwareRow.PendingTemplates"/>.
    /// </summary>
    private void StageSearchResult()
    {
        var row = _rows[_currentIndex];
        if (SearchTplResultsList.SelectedItem is not IndividualTemplate t)
        {
            SearchTplStatusLabel.Text = "Select a template from the list first.";
            SearchTplStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            return;
        }

        if (row.PendingTemplates.Any(p => p.Template.Id == t.Id))
        {
            SearchTplStatusLabel.Text = $"Already staged: {t.TemplateNumber}";
            SearchTplStatusLabel.Foreground = Avalonia.Media.Brushes.DarkRed;
            return;
        }

        // Staged-from-search templates are not job-specific by default.
        row.PendingTemplates.Add(new BulkPendingTemplate { Template = t, IsJobSpecific = false });
        RefreshPendingList(row);
        SearchTplStatusLabel.Text = $"Staged: {t.TemplateNumber}";
        SearchTplStatusLabel.Foreground = Avalonia.Media.Brushes.DarkGreen;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task BrowseLocalFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Template PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });
        if (files.Count > 0)
            TplLocalLinkBox.Text = files[0].Path.LocalPath;
    }

    /// <summary>Returns the ID of <paramref name="rootId"/> plus all recursive descendants.</summary>
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
}
