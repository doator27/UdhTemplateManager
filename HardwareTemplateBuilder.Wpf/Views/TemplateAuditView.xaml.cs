using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.TemplateAudit;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>
/// Maintenance → Template Description Audit: browse templates by manufacturer and description
/// (<see cref="TemplateAuditService"/>), preview each one, mark it OK / Needs Change with a note,
/// and change the description of the selected templates (optionally their linked items too).
/// </summary>
public partial class TemplateAuditView : UserControl
{
    /// <summary>The route of this view.</summary>
    public const string Route = "TemplateAudit";

    // Remembered while the app runs, so coming back from a hardware item keeps your place.
    private static int? _lastManufacturerId;
    private static int? _lastDescriptionId;
    private static int _lastStatusIndex;
    private static int? _lastTemplateId;

    private List<TemplateAuditRow> _rows = new();
    private List<Description> _descriptions = new();
    private List<TemplateAuditRow> _shown = new();
    private bool _loading;

    /// <summary>Raised to navigate elsewhere.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Creates the view.</summary>
    public TemplateAuditView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private sealed record MfrChoice(int? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly string[] StatusNames = { "All", "Not reviewed", "Needs change", "OK" };

    private TemplateAuditStatus? StatusFilter => StatusCombo.SelectedIndex switch
    {
        1 => TemplateAuditStatus.NotReviewed,
        2 => TemplateAuditStatus.NeedsChange,
        3 => TemplateAuditStatus.Ok,
        _ => null,
    };

    private List<TemplateAuditRow> Selected => TemplateList.SelectedItems.OfType<TemplateAuditRow>().ToList();

    private void Initialize()
    {
        _loading = true;
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var mfrs = ctx.Manufacturers.AsNoTracking().OrderBy(m => m.ManufacturerName).ToList();
            var choices = new List<MfrChoice> { new(null, "(All manufacturers)") };
            choices.AddRange(mfrs.Select(m => new MfrChoice(m.Id, m.ManufacturerName)));
            ManufacturerCombo.ItemsSource = choices;
            ManufacturerCombo.SelectedItem = choices.FirstOrDefault(c => c.Id == _lastManufacturerId) ?? choices[0];
        }
        StatusCombo.ItemsSource = StatusNames;
        StatusCombo.SelectedIndex = _lastStatusIndex;
        _loading = false;

        ManufacturerCombo.SelectionChanged += (_, e) => { e.Handled = true; if (!_loading) { _lastManufacturerId = (ManufacturerCombo.SelectedItem as MfrChoice)?.Id; RefreshDescriptions(); } };
        StatusCombo.SelectionChanged += (_, e) => { e.Handled = true; if (!_loading) { _lastStatusIndex = StatusCombo.SelectedIndex; RefreshDescriptions(); } };
        SearchBox.TextChanged += (_, _) => { if (!_loading) RefreshDescriptions(); };
        DifferCheck.Click += (_, _) => { if (!_loading) RefreshDescriptions(); };
        SubtreeCheck.Click += (_, _) => { if (!_loading) RefreshTemplates(); };
        DescriptionList.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            if (_loading) return;
            _lastDescriptionId = (DescriptionList.SelectedItem as TemplateAuditDescription)?.DescriptionId;
            RefreshTemplates();
        };
        TemplateList.SelectionChanged += async (_, e) => { e.Handled = true; if (!_loading) await ShowSelectionAsync(); };
        ItemsList.SelectionChanged += (_, e) => e.Handled = true;
        ItemsList.MouseDoubleClick += (_, _) =>
        {
            if (ItemsList.SelectedItem is ItemRow row)
                NavigationRequested?.Invoke($"HardwareItemDetail:{row.Item.ItemId}:{Route}");
        };
        SelectAllButton.Click += (_, _) => TemplateList.SelectAll();
        NextUnreviewedButton.Click += (_, _) => SelectNextUnreviewed();
        OkButton.Click += (_, _) => MarkSelected(TemplateAuditStatus.Ok, advance: true);
        NeedsChangeButton.Click += (_, _) => MarkSelected(TemplateAuditStatus.NeedsChange, advance: false);
        ClearMarkButton.Click += (_, _) => MarkSelected(TemplateAuditStatus.NotReviewed, advance: false);
        SaveNoteButton.Click += (_, _) => SaveNote();
        ChangeDescriptionButton.Click += async (_, _) => await ChangeDescriptionAsync();
        ExportButton.Click += async (_, _) => await ExportAsync();
        DescriptionsButton.Click += (_, _) => NavigationRequested?.Invoke("Descriptions");

        Reload();
    }

    private sealed record ItemRow(TemplateAuditItem Item, bool Differs)
    {
        public override string ToString() =>
            $"{Item.ModelNumber}   —  {Item.DescriptionPath}{(Differs ? "   (different)" : string.Empty)}{(Item.JobOnly ? "   [one job only]" : string.Empty)}";
    }

    /// <summary>Reads everything from the database again, keeping the selection.</summary>
    private void Reload(IReadOnlyCollection<int>? keepSelected = null)
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            _descriptions = ctx.Descriptions.AsNoTracking().ToList();
            _rows = TemplateAuditService.Load(ctx);
        }
        catch (Exception ex)
        {
            Status($"Couldn't load the templates: {ex.Message}", AppColors.Danger);
            return;
        }
        RefreshDescriptions(keepSelected);
    }

    /// <summary>The rows passing the manufacturer/status/search filters (any description).</summary>
    private List<TemplateAuditRow> BaseFiltered() =>
        TemplateAuditService.Filter(_rows, (ManufacturerCombo.SelectedItem as MfrChoice)?.Id, null, StatusFilter, SearchBox.Text, DifferCheck.IsChecked == true);

    private void RefreshDescriptions(IReadOnlyCollection<int>? keepSelected = null)
    {
        var tree = TemplateAuditService.DescriptionTree(BaseFiltered(), _descriptions);
        _loading = true;
        DescriptionList.ItemsSource = tree;
        DescriptionList.SelectedItem = tree.FirstOrDefault(d => d.DescriptionId == _lastDescriptionId) ?? tree[0];
        _loading = false;
        RefreshTemplates(keepSelected);
    }

    private void RefreshTemplates(IReadOnlyCollection<int>? keepSelected = null)
    {
        ISet<int>? descIds = null;
        if (DescriptionList.SelectedItem is TemplateAuditDescription { DescriptionId: int id })
            descIds = SubtreeCheck.IsChecked == true ? TemplateAuditService.Subtree(id, _descriptions) : new HashSet<int> { id };
        var keep = keepSelected ?? (Selected.Count > 0 ? Selected.Select(r => r.TemplateId).ToList() : _lastTemplateId is int t ? new List<int> { t } : new List<int>());

        _shown = BaseFiltered().Where(r => descIds == null || descIds.Contains(r.DescriptionId)).ToList();
        _loading = true;
        TemplateList.ItemsSource = _shown;
        foreach (var row in _shown.Where(r => keep.Contains(r.TemplateId)))
            TemplateList.SelectedItems.Add(row);
        _loading = false;

        int ok = _shown.Count(r => r.Status == TemplateAuditStatus.Ok);
        int change = _shown.Count(r => r.Status == TemplateAuditStatus.NeedsChange);
        TemplatesGroup.Header = $"Templates: {_shown.Count}   ({ok} OK, {change} need change, {_shown.Count - ok - change} not reviewed)";
        if (TemplateList.SelectedItems.Count > 0) TemplateList.ScrollIntoView(TemplateList.SelectedItems[0]!);
        _ = ShowSelectionAsync();
    }

    private async Task ShowSelectionAsync()
    {
        var sel = Selected;
        var first = sel.FirstOrDefault();
        _lastTemplateId = first?.TemplateId;
        if (first == null)
        {
            DetailsText.Text = "Select a template (Ctrl/Shift-click or Select All for several).";
            ItemsList.ItemsSource = null;
            NoteBox.Text = string.Empty;
            await PreviewPanel.LoadTemplateAsync(null);
            return;
        }

        var lines = new List<string>();
        if (sel.Count > 1) lines.Add($"{sel.Count} templates selected (actions apply to all of them). Showing {first.TemplateNumber}:");
        lines.Add($"{first.ManufacturerName}  {first.TemplateNumber}");
        lines.Add($"Description: {first.DescriptionPath}");
        lines.Add($"Door material: {first.DoorMaterial}    Pages to print: {first.PagesToPrint}{(first.OriginJobId != null ? "    (only for one job)" : string.Empty)}");
        if (first.Mark != null)
            lines.Add($"Marked {StatusNames[first.Status switch { TemplateAuditStatus.NeedsChange => 2, TemplateAuditStatus.Ok => 3, _ => 1 }]}" +
                      $"{(first.Mark.MarkedBy != null ? $" by {first.Mark.MarkedBy}" : string.Empty)} on {first.Mark.MarkedAt.ToLocalTime():d}" +
                      $"{(string.IsNullOrEmpty(first.Mark.Note) ? string.Empty : $": {first.Mark.Note}")}");
        DetailsText.Text = string.Join(Environment.NewLine, lines);
        ItemsList.ItemsSource = first.Items.Select(i => new ItemRow(i, i.DescriptionId != first.DescriptionId)).ToList();
        NoteBox.Text = sel.Count == 1 ? first.Mark?.Note ?? string.Empty : string.Empty;

        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var tpl = ctx.IndividualTemplates.AsNoTracking().FirstOrDefault(t => t.Id == first.TemplateId);
            await PreviewPanel.LoadTemplateAsync(tpl);
        }
        catch (Exception ex)
        {
            Status($"Couldn't preview the template: {ex.Message}", AppColors.Danger);
        }
    }

    private void SelectNextUnreviewed()
    {
        int start = TemplateList.SelectedIndex;
        var next = _shown.Skip(start + 1).Concat(_shown.Take(Math.Max(0, start + 1)))
            .FirstOrDefault(r => r.Status == TemplateAuditStatus.NotReviewed);
        if (next == null)
        {
            Status("Every template shown here has been reviewed.", AppColors.Success);
            return;
        }
        TemplateList.SelectedItems.Clear();
        TemplateList.SelectedItem = next;
        TemplateList.ScrollIntoView(next);
    }

    private void MarkSelected(TemplateAuditStatus status, bool advance)
    {
        var sel = Selected;
        if (sel.Count == 0) { Status("Select a template first.", AppColors.Danger); return; }
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            // A note typed for one template goes with its mark; with several selected, notes are kept.
            string? note = sel.Count == 1 ? NoteBox.Text ?? string.Empty : null;
            TemplateAuditService.Mark(ctx, sel.Select(r => r.TemplateId), status, note, SessionService.ActiveUserProfile?.UserName);
        }
        catch (Exception ex)
        {
            Status($"Couldn't save: {ex.Message}", AppColors.Danger);
            return;
        }
        Status($"Marked {sel.Count} template{(sel.Count == 1 ? "" : "s")} {StatusNames[status switch { TemplateAuditStatus.NeedsChange => 2, TemplateAuditStatus.Ok => 3, _ => 1 }].ToLowerInvariant()}.", AppColors.Success);
        int index = TemplateList.SelectedIndex;
        Reload(sel.Select(r => r.TemplateId).ToList());
        if (advance && sel.Count == 1 && _shown.Count > 0)
        {
            // Mark OK moves on to the next template that hasn't been reviewed.
            var next = _shown.Skip(Math.Max(0, index)).Concat(_shown).FirstOrDefault(r => r.Status == TemplateAuditStatus.NotReviewed);
            if (next != null)
            {
                TemplateList.SelectedItems.Clear();
                TemplateList.SelectedItem = next;
                TemplateList.ScrollIntoView(next);
            }
        }
    }

    private void SaveNote()
    {
        var sel = Selected;
        if (sel.Count == 0) { Status("Select a template first.", AppColors.Danger); return; }
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            foreach (var group in sel.GroupBy(r => r.Status))
                TemplateAuditService.Mark(ctx, group.Select(r => r.TemplateId),
                    group.Key == TemplateAuditStatus.NotReviewed ? TemplateAuditStatus.NeedsChange : group.Key,
                    NoteBox.Text ?? string.Empty, SessionService.ActiveUserProfile?.UserName);
        }
        catch (Exception ex)
        {
            Status($"Couldn't save the note: {ex.Message}", AppColors.Danger);
            return;
        }
        Status(string.IsNullOrWhiteSpace(NoteBox.Text) ? "Note cleared." : "Note saved (templates not reviewed yet are marked Needs Change).", AppColors.Success);
        Reload(sel.Select(r => r.TemplateId).ToList());
    }

    private async Task ChangeDescriptionAsync()
    {
        var sel = Selected;
        var window = this.OwnerWindow();
        if (sel.Count == 0 || window == null) { Status("Select the templates to change.", AppColors.Danger); return; }

        var picker = new DescriptionPickerWindow(_descriptions.Where(d => d.IsActive), leafOnly: false);
        if (await picker.ShowDialogAsync<int?>(window) is not int descId || descId == DescriptionPickerWindow.TopLevelId) return;
        var byId = _descriptions.ToDictionary(d => d.Id);
        var path = TemplateAuditService.Path(descId, byId);
        bool alsoItems = AlsoItemsCheck.IsChecked == true;
        var question = $"Change the description of {sel.Count} template{(sel.Count == 1 ? "" : "s")} to\n\n{path}\n\n" +
                       (alsoItems ? "Linked hardware items that had the template's old description move too.\n\n" : string.Empty) +
                       "This changes where they sort in the next generated packages.";
        if (!await DialogHelper.ConfirmAsync(window, question, "Change Description")) return;

        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var (t, i) = TemplateAuditService.SetDescription(ctx, sel.Select(r => r.TemplateId), descId, alsoItems);
            Status($"Moved {t} template{(t == 1 ? "" : "s")}{(alsoItems ? $" and {i} hardware item{(i == 1 ? "" : "s")}" : string.Empty)} to {path}.", AppColors.Success);
        }
        catch (Exception ex)
        {
            Status($"Couldn't change the description: {ex.Message}", AppColors.Danger);
            return;
        }
        Reload(sel.Select(r => r.TemplateId).ToList());
    }

    private async Task ExportAsync()
    {
        var path = FilePickers.SaveFile(this.OwnerWindow(), "Export Template Audit", $"template-audit-{DateTime.Now:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
        if (path == null) return;
        try
        {
            await File.WriteAllTextAsync(path, TemplateAuditService.ToCsv(_shown));
            Status($"Exported {_shown.Count} templates to {path}", AppColors.Success);
        }
        catch (Exception ex)
        {
            Status($"Couldn't export: {ex.Message}", AppColors.Danger);
        }
    }

    private void Status(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }
}
