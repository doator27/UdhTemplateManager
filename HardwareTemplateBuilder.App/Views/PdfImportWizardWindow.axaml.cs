using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// "Parse PDF into Bulk Add" wizard. Reads the selected job attachment
/// (<see cref="HardwarePdfImportService"/>), shows a summary, then steps through each distinct
/// callout that couldn't be identified (existing item + label, new item, or leave out). Finish
/// records the answers so they're recognised next time and adds everything to the job's Bulk Add
/// draft (skipping lines already in the draft or on the job). Returns true when lines were added.
/// </summary>
public partial class PdfImportWizardWindow : Window
{
    private readonly int _jobId;
    private readonly int? _releaseId;
    private readonly string _pdfPath;
    private readonly CancellationTokenSource _cts = new();
    private HardwareImportSession? _session;
    private List<HardwareImportGroup> _steps = new();
    private int _index = -1; // -1 = summary page
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
        Opened += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task InitializeAsync()
    {
        BackButton.Click += (_, _) => Go(-1);
        NextButton.Click += (_, _) => Go(+1);
        FinishButton.Click += async (_, _) => await FinishAsync();
        CancelButton.Click += (_, _) => Close((bool?)false);
        ReviewAllCheck.IsCheckedChanged += (_, _) => UpdateButtons();
        ExistingRadio.IsCheckedChanged += (_, _) => ShowMode();
        NewRadio.IsCheckedChanged += (_, _) => ShowMode();
        SkipRadio.IsCheckedChanged += (_, _) => ShowMode();
        ManufacturerCombo.SelectionChanged += (_, _) => { if (!_loadingStep) RefreshItems(keepSelection: false); };
        SearchBox.TextChanged += (_, _) => { if (!_loadingStep) RefreshItems(keepSelection: true); };
        ItemsList.SelectionChanged += (_, _) =>
        {
            if (!_loadingStep && ItemsList.SelectedItem is ImportItemChoice c && Current is { } g)
                LabelBox.Text = _session!.LabelFor(g, c);
        };
        PickDescriptionButton.Click += async (_, _) => await PickDescriptionAsync();

        HeaderText.Text = "Reading the PDF...";
        var progress = new Progress<string>(s => SummaryText.Text = s);
        try
        {
            IPdfOcrEngine? ocr = null;
#if WINDOWS10_0_19041_0_OR_GREATER
            ocr = new HardwareTemplateBuilder.Shared.WindowsPdfOcrEngine();
#endif
            var analysis = await Task.Run(() => HardwarePdfImportService.AnalyzeAsync(_pdfPath, ocr, progress, _cts.Token));
            _session = await Task.Run(() => new HardwareImportSession(analysis, _jobId, _releaseId));
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

    private HardwareImportGroup? Current => _index >= 0 && _index < _steps.Count ? _steps[_index] : null;

    // ── Pages ───────────────────────────────────────────────────────────────

    private void ShowSummary()
    {
        _index = -1;
        SummaryPanel.IsVisible = true;
        StepPanel.IsVisible = false;
        HeaderText.Text = "Summary";
        SummaryText.Text = _session!.SummaryText;
        SummaryList.ItemsSource = _session.Analysis.Groups.Select(_session.SummaryLine).ToList();
        UpdateButtons();
    }

    private void Go(int delta)
    {
        if (_session == null) return;
        if (_index >= 0 && delta > 0 && !SaveStep()) return;
        if (_index >= 0 && delta < 0) SaveStep(validate: false);

        if (_index == -1 && delta > 0)
        {
            _steps = _session.BuildSteps(ReviewAllCheck.IsChecked == true);
            if (_steps.Count == 0) return;
            ShowStep(0);
            return;
        }
        int next = _index + delta;
        if (next < 0) ShowSummary();
        else if (next < _steps.Count) ShowStep(next);
    }

    private void ShowStep(int index)
    {
        _index = index;
        var g = _steps[index];
        _loadingStep = true;
        try
        {
            SummaryPanel.IsVisible = false;
            StepPanel.IsVisible = true;
            HeaderText.Text = $"Item {index + 1} of {_steps.Count}: {g.GenericName}";
            SourceText.Text = _session!.SourceText(g);
            ReasonText.Text = g.Status switch
            {
                ImportMatchStatus.Matched => $"Identified automatically: {g.Reason}",
                ImportMatchStatus.Skipped => g.Reason,
                _ => g.Reason,
            };

            ManufacturerCombo.ItemsSource = _session.Manufacturers;
            ManufacturerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
            int? mfr = g.CreateNew ? g.NewManufacturerId : _session.Item(g.HardwareItemId)?.ManufacturerId ?? g.ManufacturerId;
            ManufacturerCombo.SelectedItem = _session.Manufacturers.FirstOrDefault(m => m.Key == mfr);
            if (ManufacturerCombo.SelectedItem is KeyValuePair<int, string> { Key: 0 }) ManufacturerCombo.SelectedItem = null;

            SearchBox.Text = string.Empty;
            LabelBox.Text = g.Label;
            DescriptionBox.Text = _session.DescriptionPath(g.NewDescriptionId);
            DescriptionBox.Tag = g.NewDescriptionId;
            ModelBox.Text = string.IsNullOrEmpty(g.NewModelNumber) ? ImportText.StripSize(g.Catalog) : g.NewModelNumber;

            if (g.Skip) SkipRadio.IsChecked = true;
            else if (g.CreateNew || (g.HardwareItemId == null && g.Suggestions.Count == 0)) NewRadio.IsChecked = true;
            else ExistingRadio.IsChecked = true;

            RefreshItems(keepSelection: false, preferItemId: g.HardwareItemId, preferLabel: g.Label);
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
        NewPanel.IsVisible = NewRadio.IsChecked == true;
        ExistingPanel.IsVisible = ExistingRadio.IsChecked == true;
        bool skip = SkipRadio.IsChecked == true;
        ManufacturerCombo.IsEnabled = !skip;
        LabelBox.IsEnabled = !skip;
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
            var model = (ModelBox.Text ?? string.Empty).Trim();
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
            g.Label = (LabelBox.Text ?? string.Empty).Trim();
            return true;
        }
        if (ItemsList.SelectedItem is not ImportItemChoice choice)
        {
            if (validate) StatusLabel.Text = "Pick the hardware item from the list (or choose New hardware item / Leave this out).";
            return !validate;
        }
        g.Skip = false;
        g.CreateNew = false;
        g.HardwareItemId = choice.ItemId;
        g.Label = (LabelBox.Text ?? string.Empty).Trim();
        return true;
    }

    private async Task PickDescriptionAsync()
    {
        if (_session == null) return;
        var picker = new DescriptionPickerWindow(_session.Descriptions.Where(d => d.IsActive), leafOnly: false);
        var id = await picker.ShowDialog<int?>(this);
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
            int toReview = ready ? _session!.BuildSteps(ReviewAllCheck.IsChecked == true).Count : 0;
            NextButton.IsEnabled = toReview > 0;
            NextButton.Content = toReview > 0 ? $"Review {toReview} item(s) >" : "Next >";
            FinishButton.IsEnabled = ready && _session!.ReviewCount == 0 && ReviewAllCheck.IsChecked != true;
        }
        else
        {
            NextButton.IsEnabled = _index < _steps.Count - 1;
            NextButton.Content = "Next >";
            FinishButton.IsEnabled = _index == _steps.Count - 1;
        }
    }

    // ── Finish ──────────────────────────────────────────────────────────────

    private async Task FinishAsync()
    {
        if (_session == null) return;
        if (_index >= 0 && !SaveStep()) return;

        try
        {
            if (_steps.Count > 0) _session.RecordLearning(_steps);

            var lines = _session.DraftLines();
            int added = 0, alreadyThere = 0;
            var draft = BulkDraftService.Load(_jobId);
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
            BulkDraftService.Save(_jobId, draft);

            int left = _session.Analysis.Groups.Count(g => g.Skip);
            await DialogHelper.ShowInfoAsync(this,
                $"Added {added} line(s) to Bulk Add." +
                (alreadyThere > 0 ? $"\n{alreadyThere} were already in Bulk Add or on the job, so they were skipped." : string.Empty) +
                (left > 0 ? $"\n{left} PDF item(s) were left out." : string.Empty),
                "Parse PDF into Bulk Add");
            Close((bool?)(added > 0 || alreadyThere > 0));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Couldn't add to Bulk Add: {ex.Message}";
        }
    }
}
