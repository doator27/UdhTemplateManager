using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HardwareTemplateBuilder.Core.Services.Catalogs;
using HardwareTemplateBuilder.Core.Services.Import;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>
/// "Parse PDF into Bulk Add" wizard. Reads the selected job attachment
/// (<see cref="HardwarePdfImportService"/>), shows a summary (callouts already on the job or in
/// Bulk Add are skipped), asks about each set of similar callouts (which to keep, one item or
/// separate items), then steps through each distinct callout that couldn't be identified
/// (existing item + label, new item, or leave out). Finish
/// records the answers so they're recognised next time and adds everything to the job's Bulk Add
/// draft (skipping lines already in the draft or on the job). Returns true when lines were added.
/// </summary>
public partial class PdfImportWizardWindow : Win95Window
{
    private readonly int _jobId;
    private readonly int? _releaseId;
    private readonly string _pdfPath;
    private readonly CancellationTokenSource _cts = new();
    private HardwareImportSession? _session;
    private List<HardwareImportGroup> _steps = new();
    private int _index = -1; // -1 = summary, then one page per similar set, then one per callout
    private readonly List<(ImportLabelChoice Choice, CheckBox Box, TextBox Text)> _labelRows = new();
    private bool _loadingStep;

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public PdfImportWizardWindow() : this(0, null, string.Empty, string.Empty) { }

    /// <summary>Creates the wizard.</summary>
    /// <param name="jobId">The job to add hardware to.</param>
    /// <param name="releaseId">The job's active release (null = base list).</param>
    /// <param name="pdfPath">The PDF to read.</param>
    /// <param name="fileName">Its display name.</param>
    public PdfImportWizardWindow(int jobId, int? releaseId, string pdfPath, string fileName)
    {
        _jobId = jobId;
        _releaseId = releaseId;
        _pdfPath = pdfPath;
        InitializeComponent();
        Title = $"Parse PDF into Bulk Add - {fileName}";
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task InitializeAsync()
    {
        BackButton.Click += (_, _) => Go(-1);
        NextButton.Click += (_, _) => Go(+1);
        FinishButton.Click += async (_, _) => await FinishAsync();
        CancelButton.Click += (_, _) => CloseWith(false);
        ReviewAllCheck.Click += (_, _) => UpdateButtons();
        SameItemButton.Click += (_, _) => SortCluster(c => c.AddItem(TickedInCluster()));
        EachItemButton.Click += (_, _) => SortCluster(c => c.AddEach(TickedInCluster()));
        LeaveOutButton.Click += (_, _) => SortCluster(c => c.Leave(TickedInCluster()));
        ResetClusterButton.Click += (_, _) => SortCluster(c => c.Reset());
        ModelBox.LostFocus += (_, _) => { if (!_loadingStep) RefreshLabelRows(); };
        ExistingRadio.Checked += (_, _) => ShowMode();
        NewRadio.Checked += (_, _) => ShowMode();
        SkipRadio.Checked += (_, _) => ShowMode();
        ManufacturerCombo.SelectionChanged += (_, e) => { e.Handled = true; if (!_loadingStep) { RefreshItems(keepSelection: false); RefreshLabelRows(); } };
        SearchBox.TextChanged += (_, _) => { if (!_loadingStep) RefreshItems(keepSelection: true); };
        ItemsList.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            if (!_loadingStep && ItemsList.SelectedItem is ImportItemChoice c && Current is { } g)
            {
                LabelBox.Text = _session!.LabelFor(g, c);
                RefreshLabelRows();
            }
        };
        PickDescriptionButton.Click += async (_, _) => await PickDescriptionAsync();
        AssemblyButton.Click += async (_, _) => await MakeAssemblyAsync();
        CatalogButton.Click += async (_, _) =>
        {
            if (Current is { CatalogInfo: { } info } g)
                await new CatalogLookupWindow(info.ManufacturerId, _session!.ManufacturerName(info.ManufacturerId), ImportText.StripNotesAndSize(g.Catalog), allowChoose: false)
                    .ShowDialogAsync(this);
        };

        HeaderText.Text = "Reading the PDF...";
        var progress = new Progress<string>(s => SummaryText.Text = s);
        try
        {
            IPdfOcrEngine? ocr = null;
#if WINDOWS10_0_19041_0_OR_GREATER
            ocr = new HardwareTemplateBuilder.Shared.WindowsPdfOcrEngine();
#endif
            var analysis = await Task.Run(() => HardwarePdfImportService.AnalyzeAsync(_pdfPath, ocr, progress, _cts.Token));
            var draft = BulkDraftService.Load(_jobId, _releaseId);
            var draftLines = draft.Items
                .SelectMany(i => (i.Labels.Count > 0 ? i.Labels.Select(l => l.CustomLabel) : new[] { string.Empty })
                    .Select(label => new ImportDraftLine(i.ManufacturerId, i.DescriptionId ?? 0, i.ModelNumber, label ?? string.Empty, null)))
                .ToList();
            _session = await Task.Run(() => new HardwareImportSession(analysis, _jobId, _releaseId, draftLines));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            HeaderText.Text = "Couldn't read the PDF";
            SummaryText.Text = ex.Message;
            return;
        }

        HeaderText.Text = "Summary";
        ShowSummary();
    }

    private int ClusterCount => _session?.Clusters.Count ?? 0;

    private SimilarCluster? CurrentCluster => _index >= 0 && _index < ClusterCount ? _session!.Clusters[_index] : null;

    private HardwareImportGroup? Current =>
        _index >= ClusterCount && _index - ClusterCount < _steps.Count ? _steps[_index - ClusterCount] : null;

    // ── Pages ───────────────────────────────────────────────────────────────

    private void ShowSummary()
    {
        _index = -1;
        SummaryPanel.SetVisible(true);
        StepPanel.SetVisible(false);
        ClusterPanel.SetVisible(false);
        HeaderText.Text = "Summary";
        SummaryText.Text = _session!.SummaryText;
        SummaryList.ItemsSource = _session.Analysis.Groups.Select(_session.SummaryLine).ToList();
        UpdateButtons();
    }

    private void Go(int delta)
    {
        if (_session == null) return;
        if (CurrentCluster is { } cur)
        {
            if (delta > 0 && !cur.IsDone)
            {
                StatusLabel.Text = "Sort these items first: tick the ones that are the same item, or make them their own items or leave them out.";
                return;
            }
            HardwareImportSimilarity.Apply(cur);
        }
        else if (_index >= 0 && delta > 0 && !SaveStep()) return;
        else if (_index >= 0 && delta < 0) SaveStep(validate: false);

        int next = _index + delta;
        if (next < 0) { ShowSummary(); return; }
        if (next < ClusterCount) { ShowCluster(next); return; }
        if (_index < ClusterCount)
        {
            // Leaving the summary or the similar-items pages: the callouts to step through
            // depend on what was kept and combined.
            _steps = _session.BuildSteps(ReviewAllCheck.IsChecked == true);
        }
        // Skip callouts that became part of an assembly since the list was built.
        int dir = delta > 0 ? 1 : -1;
        while (next >= ClusterCount && next - ClusterCount < _steps.Count && !HardwareImportSession.InPlay(_steps[next - ClusterCount]))
            next += dir;
        if (next < 0) { ShowSummary(); return; }
        if (next < ClusterCount) { ShowCluster(next); return; }
        if (next - ClusterCount < _steps.Count) ShowStep(next - ClusterCount);
        else UpdateButtons();
    }

    /// <summary>Shows one set of similar callouts: the unsorted ones with tick boxes, then the items made so far.</summary>
    private void ShowCluster(int index)
    {
        _index = index;
        var c = _session!.Clusters[index];
        SummaryPanel.SetVisible(false);
        StepPanel.SetVisible(false);
        ClusterPanel.SetVisible(true);
        var first = c.Groups[0];
        var who = first.ManufacturerId is int m ? _session.ManufacturerName(m) : first.ManufacturerCode;
        HeaderText.Text = $"Similar items {index + 1} of {ClusterCount}";
        ClusterIntro.Text =
            $"These {c.Groups.Count} {who} callouts look alike. Tick the ones that are the same item and click " +
            "\"Ticked Are the Same Item\"; repeat until they're all sorted. You can also make ticked ones their own items " +
            "or leave them out of this job. When only one is left it becomes its own item.";
        RenderCluster(c);
        StatusLabel.Text = string.Empty;
        UpdateButtons();
    }

    private void RenderCluster(SimilarCluster c)
    {
        ClusterList.Children.Clear();
        var unsorted = c.Unsorted;
        if (unsorted.Count > 0)
            ClusterList.Children.Add(new TextBlock { Text = "Not sorted yet:", FontWeight = FontWeights.Bold });
        foreach (var g in unsorted)
            ClusterList.Children.Add(new CheckBox
            {
                Content = new TextBlock { Text = _session!.ClusterLine(g), TextWrapping = TextWrapping.Wrap },
                Tag = g,
            });

        var sorted = c.Items.Select(i => (Text: i.Count == 1 ? $"Own item: {i[0].Catalog}" : $"Same item: {string.Join("  +  ", i.Select(g => g.Catalog))}", First: i[0]))
            .Concat(c.Groups.Where(c.LeftOut.Contains).Select(g => (Text: $"Left out: {g.Catalog}", First: g)))
            .ToList();
        if (sorted.Count > 0)
            ClusterList.Children.Add(new TextBlock { Text = "Sorted:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var (text, firstOf) in sorted)
        {
            var undo = new Button { Content = "Undo", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            undo.Click += (_, _) => SortCluster(x => x.Undo(firstOf));
            var row = new DockPanel();
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            ClusterList.Children.Add(row);
        }
        ClusterProgress.Text = c.IsDone
            ? (unsorted.Count == 1 ? $"Done: \"{unsorted[0].Catalog}\" will be its own item." : "All sorted.")
            : $"{unsorted.Count} still to sort.";
        SameItemButton.IsEnabled = EachItemButton.IsEnabled = LeaveOutButton.IsEnabled = unsorted.Count > 0;
    }

    private List<HardwareImportGroup> TickedInCluster() =>
        ClusterList.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (HardwareImportGroup)b.Tag).ToList();

    /// <summary>Changes the current similar-items page's sorting, applies it and redraws the page.</summary>
    private void SortCluster(Action<SimilarCluster> change)
    {
        if (CurrentCluster is not { } c) return;
        change(c);
        HardwareImportSimilarity.Apply(c);
        RenderCluster(c);
        StatusLabel.Text = string.Empty;
        UpdateButtons();
    }

    private void ShowStep(int index)
    {
        _index = ClusterCount + index;
        var g = _steps[index];
        _loadingStep = true;
        try
        {
            SummaryPanel.SetVisible(false);
            ClusterPanel.SetVisible(false);
            StepPanel.SetVisible(true);
            HeaderText.Text = $"Item {index + 1} of {_steps.Count}: {g.GenericName}";
            SourceText.Text = _session!.SourceText(g);
            ShowAssembly(g);
            CatalogPanel.SetVisible(g.CatalogInfo != null);
            CatalogText.Text = g.CatalogInfo?.Summary ?? string.Empty;
            ReasonText.Text = g.Status switch
            {
                ImportMatchStatus.Matched => $"Identified automatically: {g.Reason}",
                ImportMatchStatus.Skipped => g.Reason,
                _ => g.Reason,
            };

            ManufacturerCombo.ItemsSource = _session.Manufacturers;
            ManufacturerCombo.DisplayMemberPath = "Value";
            int? mfr = g.CreateNew ? g.NewManufacturerId : _session.Item(g.HardwareItemId)?.ManufacturerId ?? g.ManufacturerId;
            ManufacturerCombo.SelectedItem = _session.Manufacturers.FirstOrDefault(m => m.Key == mfr);
            if (ManufacturerCombo.SelectedItem is KeyValuePair<int, string> { Key: 0 }) ManufacturerCombo.SelectedItem = null;

            SearchBox.Text = string.Empty;
            LabelBox.Text = g.Label;
            DescriptionBox.Text = _session.DescriptionPath(g.NewDescriptionId);
            DescriptionBox.Tag = g.NewDescriptionId;
            ModelBox.Text = string.IsNullOrEmpty(g.NewModelNumber) ? ImportText.StripNotesAndSize(g.Catalog) : g.NewModelNumber;

            if (g.Skip) SkipRadio.IsChecked = true;
            else if (g.CreateNew || (g.HardwareItemId == null && g.Suggestions.Count == 0)) NewRadio.IsChecked = true;
            else ExistingRadio.IsChecked = true;

            RefreshItems(keepSelection: false, preferItemId: g.HardwareItemId, preferLabel: g.Label);
            _labelRows.Clear();
        }
        finally
        {
            _loadingStep = false;
        }
        ShowMode();
        StatusLabel.Text = string.Empty;
        UpdateButtons();
    }

    private void ShowMode()
    {
        NewPanel.SetVisible(NewRadio.IsChecked == true);
        ExistingPanel.SetVisible(ExistingRadio.IsChecked == true);
        bool skip = SkipRadio.IsChecked == true;
        ManufacturerCombo.IsEnabled = !skip;
        LabelBox.IsEnabled = !skip;
        if (!_loadingStep) RefreshLabelRows();
    }

    /// <summary>Copies the label rows' ticks and texts into their choices.</summary>
    private void SyncLabelRows()
    {
        foreach (var (choice, box, text) in _labelRows)
        {
            choice.Include = box.IsChecked == true;
            choice.Text = (text.Text ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// Shows a tick box and editable label per label when the item covers several (combined
    /// look-alike callouts, or labels used with the chosen item before); otherwise the single label box.
    /// </summary>
    private void RefreshLabelRows()
    {
        if (_session == null || Current is not { } g) return;
        SyncLabelRows();
        var previous = _labelRows.Select(r => r.Choice).ToList();
        if (previous.Count == 0) previous = g.LabelChoices.ToList();

        List<ImportLabelChoice> rows = new();
        if (SkipRadio.IsChecked != true)
        {
            if (NewRadio.IsChecked == true)
            {
                int? mfr = ManufacturerCombo.SelectedItem is KeyValuePair<int, string> kv && kv.Key != 0 ? kv.Key : null;
                rows = _session.BuildLabelChoices(g, null, mfr, (ModelBox.Text ?? string.Empty).Trim(), previous);
            }
            else if (ItemsList.SelectedItem is ImportItemChoice c && _session.Item(c.ItemId) is { } item)
            {
                rows = _session.BuildLabelChoices(g, c.ItemId, item.ManufacturerId, item.ModelNumber, previous, c.Label);
            }
        }

        _labelRows.Clear();
        LabelRows.Children.Clear();
        foreach (var r in rows)
        {
            var box = new CheckBox { IsChecked = r.Include, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var text = new TextBox { Text = r.Text };
            Watermark.SetText(text, "Label (blank = none)");
            var origin = new TextBlock { Text = r.Origin, Foreground = AppColors.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Left);
            DockPanel.SetDock(origin, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(origin);
            row.Children.Add(text);
            LabelRows.Children.Add(row);
            _labelRows.Add((r, box, text));
        }
        bool many = rows.Count > 0;
        LabelsPanel.SetVisible(many);
        LabelBox.SetVisible(!many);
        LabelCaption.SetVisible(!many);
    }

    private void RefreshItems(bool keepSelection, int? preferItemId = null, string? preferLabel = null)
    {
        var g = Current;
        if (g == null || _session == null) return;
        var previous = keepSelection ? ItemsList.SelectedItem as ImportItemChoice : null;
        int? mfr = ManufacturerCombo.SelectedItem is KeyValuePair<int, string> kv && kv.Key != 0 ? kv.Key : null;
        var choices = _session.Choices(g, mfr, SearchBox.Text);

        bool was = _loadingStep;
        _loadingStep = true;
        ItemsList.ItemsSource = choices;
        int? wantId = preferItemId ?? previous?.ItemId;
        var want = choices.FirstOrDefault(c => c.ItemId == wantId && string.Equals(c.Label ?? string.Empty, preferLabel ?? previous?.Label ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                   ?? choices.FirstOrDefault(c => c.ItemId == wantId);
        ItemsList.SelectedItem = want;
        if (want != null) ItemsList.ScrollIntoView(want);
        _loadingStep = was;
    }

    /// <summary>Copies the page into the current group. Returns false (with a message) when incomplete.</summary>
    private bool SaveStep(bool validate = true)
    {
        var g = Current;
        if (g == null || _session == null) return true;
        int? mfr = ManufacturerCombo.SelectedItem is KeyValuePair<int, string> kv && kv.Key != 0 ? kv.Key : null;

        if (SkipRadio.IsChecked == true)
        {
            g.Skip = true;
            g.CreateNew = false;
            return true;
        }
        if (NewRadio.IsChecked == true)
        {
            var desc = DescriptionBox.Tag as int?;
            var model = ModelBox.Text.Trim();
            if (validate && (mfr == null || desc == null || model.Length == 0))
            {
                StatusLabel.Text = "For a new item choose the manufacturer and description and enter the model number.";
                return false;
            }
            g.Skip = false;
            g.CreateNew = true;
            g.NewManufacturerId = mfr;
            g.NewDescriptionId = desc;
            g.NewModelNumber = model;
            return SaveLabels(g, validate);
        }
        if (ItemsList.SelectedItem is not ImportItemChoice choice)
        {
            if (validate) StatusLabel.Text = "Pick the hardware item from the list (or choose New hardware item / Leave this out).";
            return !validate;
        }
        g.Skip = false;
        g.CreateNew = false;
        g.HardwareItemId = choice.ItemId;
        return SaveLabels(g, validate);
    }

    /// <summary>Saves the label box, or the ticked label rows (one line each) when shown.</summary>
    private bool SaveLabels(HardwareImportGroup g, bool validate)
    {
        g.LabelChoices.Clear();
        if (_labelRows.Count == 0)
        {
            g.Label = LabelBox.Text.Trim();
            return true;
        }
        SyncLabelRows();
        g.LabelChoices.AddRange(_labelRows.Select(r => r.Choice));
        var ticked = g.LabelChoices.Where(r => r.Include).ToList();
        if (validate && ticked.Count == 0)
        {
            StatusLabel.Text = "Tick at least one label for the cover sheet (or choose Leave this out).";
            return false;
        }
        g.Label = ticked.FirstOrDefault()?.Text ?? string.Empty;
        return true;
    }

    /// <summary>Shows whether the current callout is an assembly (or was one last time).</summary>
    private void ShowAssembly(HardwareImportGroup g)
    {
        AssemblyButton.Content = g.IsAssembly ? "Edit Assembly..." : "Make Assembly...";
        AssemblyText.Text = g.IsAssembly
            ? $"Assembly with: {string.Join(" | ", g.AssemblyParts.Select(p => p.Catalog))}. Choose below the hardware item the assembly is."
            : g.SuggestedAssemblyParts.Count > 0
                ? $"Last time this was an assembly with: {string.Join(" | ", g.SuggestedAssemblyParts.Select(p => p.Catalog))}. Make Assembly... to use it again."
                : "Is this item an assembly with other items in its heading? Make Assembly... puts them on one line.";
    }

    /// <summary>
    /// Opens the assembly window for the current callout: the ticked items become its parts
    /// (left out on their own) and its label becomes the description plus every callout joined
    /// with " | ". The user then picks the hardware item the assembly is on this page.
    /// </summary>
    private async Task MakeAssemblyAsync()
    {
        if (_session == null || Current is not { } g) return;
        SaveStep(validate: false);
        var choice = await new AssemblyWindow(_session, g).ShowDialogAsync<AssemblyChoice>(this);
        if (choice == null) return;
        HardwareImportAssemblies.Apply(g, choice.Parts, choice.Description, choice.DescriptionId);
        if (g.IsAssembly && choice.DescriptionId is int did && DescriptionBox.Tag is not int)
        {
            // A new item for the assembly starts with the assembly's description.
            g.NewDescriptionId = did;
            DescriptionBox.Tag = did;
            DescriptionBox.Text = _session.DescriptionPath(did);
        }
        LabelBox.Text = g.Label;
        _labelRows.Clear();
        RefreshLabelRows();
        SourceText.Text = _session.SourceText(g);
        ShowAssembly(g);
        StatusLabel.Text = g.IsAssembly && SkipRadio.IsChecked == true
            ? "This item is set to be left out: choose the hardware item the assembly is."
            : string.Empty;
        UpdateButtons();
    }

    private async Task PickDescriptionAsync()
    {
        if (_session == null) return;
        var picker = new DescriptionPickerWindow(_session.Descriptions.Where(d => d.IsActive), leafOnly: false);
        var id = await picker.ShowDialogAsync<int?>(this);
        if (id is int picked)
        {
            DescriptionBox.Tag = picked;
            DescriptionBox.Text = _session.DescriptionPath(picked);
        }
    }

    private void UpdateButtons()
    {
        bool ready = _session != null && _session.Analysis.Groups.Count > 0;
        BackButton.IsEnabled = _index >= 0;
        if (_index < 0)
        {
            int toReview = ready ? ClusterCount + _session!.BuildSteps(ReviewAllCheck.IsChecked == true).Count : 0;
            NextButton.IsEnabled = toReview > 0;
            NextButton.Content = toReview > 0 ? $"Review {toReview} item(s) >" : "Next >";
            FinishButton.IsEnabled = ready && _session!.ReviewCount == 0 && ClusterCount == 0 && ReviewAllCheck.IsChecked != true;
        }
        else if (_index < ClusterCount)
        {
            bool done = CurrentCluster!.IsDone;
            bool more = _index < ClusterCount - 1 || _session!.BuildSteps(ReviewAllCheck.IsChecked == true).Count > 0;
            NextButton.IsEnabled = done && more;
            NextButton.Content = "Next >";
            FinishButton.IsEnabled = done && !more;
        }
        else
        {
            int step = _index - ClusterCount;
            bool more = _steps.Skip(step + 1).Any(HardwareImportSession.InPlay);
            NextButton.IsEnabled = more;
            NextButton.Content = "Next >";
            FinishButton.IsEnabled = !more;
        }
    }

    // ── Finish ──────────────────────────────────────────────────────────────

    private async Task FinishAsync()
    {
        if (_session == null) return;
        if (CurrentCluster is { } cur)
        {
            if (!cur.IsDone) return;
            HardwareImportSimilarity.Apply(cur);
        }
        else if (Current != null && !SaveStep()) return;

        try
        {
            _session.RecordLearning(_steps);

            var lines = _session.DraftLines();
            int added = 0, alreadyThere = 0;
            var draft = BulkDraftService.Load(_jobId, _releaseId);
            foreach (var line in lines)
            {
                if (_session.IsOnJob(line)) { alreadyThere++; continue; }
                var item = draft.Items.FirstOrDefault(i =>
                    i.ManufacturerId == line.ManufacturerId && i.DescriptionId == line.DescriptionId &&
                    string.Equals(i.ModelNumber.Trim(), line.ModelNumber, StringComparison.OrdinalIgnoreCase));
                bool created = item == null;
                if (item == null)
                {
                    item = new BulkDraftItem { ManufacturerId = line.ManufacturerId, DescriptionId = line.DescriptionId, ModelNumber = line.ModelNumber };
                    draft.Items.Add(item);
                }
                var labelKey = HardwareImportSession.NormLabel(line.Label);
                // A draft item with no labels stands for one unlabelled line.
                bool hasLabel = !created && (item.Labels.Count == 0
                    ? labelKey.Length == 0
                    : item.Labels.Any(l => HardwareImportSession.NormLabel(l.CustomLabel) == labelKey));
                if (hasLabel) { alreadyThere++; continue; }
                if (!created && item.Labels.Count == 0) item.Labels.Add(new BulkDraftLabel { CustomLabel = string.Empty });
                item.Labels.Add(new BulkDraftLabel { CustomLabel = line.Label });
                if (!draft.ManufacturerIds.Contains(line.ManufacturerId)) draft.ManufacturerIds.Add(line.ManufacturerId);
                added++;
            }
            BulkDraftService.Save(_jobId, _releaseId, draft);

            int left = _session.Analysis.Groups.Count(g => g.Skip && !g.IsAlreadyAdded);
            int had = _session.Analysis.Groups.Count(g => g.IsAlreadyAdded);
            await DialogHelper.ShowInfoAsync(this,
                $"Added {added} line(s) to Bulk Add." +
                (alreadyThere > 0 ? $"\n{alreadyThere} were already in Bulk Add or on the job, so they were skipped." : string.Empty) +
                (had > 0 ? $"\n{had} PDF item(s) were already on the job or in Bulk Add." : string.Empty) +
                (left > 0 ? $"\n{left} PDF item(s) were left out." : string.Empty),
                "Parse PDF into Bulk Add");
            CloseWith(added > 0 || alreadyThere > 0);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Couldn't add to Bulk Add: {ex.Message}";
        }
    }
}
