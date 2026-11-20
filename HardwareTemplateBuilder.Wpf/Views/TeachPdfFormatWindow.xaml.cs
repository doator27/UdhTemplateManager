using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>
/// Teaches the app a new PDF format (<see cref="CustomPdfFormat"/>): shows the PDF as the app reads
/// it, the user marks the column header, an example item line and a heading line and says what each
/// column holds, previews the result over the whole PDF and saves it. Returns true when saved.
/// </summary>
public partial class TeachPdfFormatWindow : Win95Window
{
    private readonly string _pdfPath;
    private readonly CustomPdfFormat? _editing;
    private readonly CancellationTokenSource _cts = new();
    private List<List<string>> _pages = new();
    private int _page;
    private string? _headerLine;
    private string? _exampleLine;
    private readonly List<(PdfFormatColumn Column, ComboBox Combo)> _columnRows = new();

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public TeachPdfFormatWindow() : this(string.Empty, null) { }

    /// <summary>Creates the window.</summary>
    /// <param name="pdfPath">A PDF of the format.</param>
    /// <param name="editing">The format to change (null = a new one).</param>
    public TeachPdfFormatWindow(string pdfPath, CustomPdfFormat? editing)
    {
        _pdfPath = pdfPath;
        _editing = editing;
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private sealed record FieldItem(PdfFormatField Field, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly List<FieldItem> FieldItems = CustomPdfFormats.FieldChoices.Select(c => new FieldItem(c.Field, c.Name)).ToList();

    private async Task InitializeAsync()
    {
        PrevPageButton.Click += (_, _) => ShowPage(_page - 1);
        NextPageButton.Click += (_, _) => ShowPage(_page + 1);
        HeaderButton.Click += (_, _) => { if (SelectedLine() is { } l) { _headerLine = l; Rebuild(); } };
        ClearHeaderButton.Click += (_, _) => { _headerLine = null; Rebuild(); };
        ExampleButton.Click += (_, _) => { if (SelectedLine() is { } l) { _exampleLine = l; Rebuild(); } };
        HeadingButton.Click += (_, _) =>
        {
            if (SelectedLine() is not { } l) return;
            var words = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // The heading's fixed start: words before the first one with a digit ("Heading #").
            var prefix = string.Join(" ", words.TakeWhile(w => !w.Any(char.IsDigit)));
            if (prefix.Length == 0 && words.Length > 0) prefix = new string(words[0].TakeWhile(c => !char.IsDigit(c)).ToArray());
            HeadingBox.Text = prefix.Length > 0 ? prefix : words.FirstOrDefault() ?? string.Empty;
            Status("Heading set. Edit the text if headings start differently.", AppColors.Muted);
        };
        SkipButton.Click += (_, _) =>
        {
            if (SelectedLine() is not { } l) return;
            var first = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first == null) return;
            var lines = (SkipBox.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!lines.Contains(first, StringComparer.OrdinalIgnoreCase)) lines.Add(first);
            SkipBox.Text = string.Join(Environment.NewLine, lines);
        };
        PreviewButton.Click += (_, _) => Preview();
        SaveButton.Click += async (_, _) => await SaveAsync();
        CancelButton.Click += (_, _) => CloseWith(false);

        PageLabel.Text = "Reading...";
        try
        {
            IPdfOcrEngine? ocr = null;
#if WINDOWS10_0_19041_0_OR_GREATER
            ocr = new HardwareTemplateBuilder.Shared.WindowsPdfOcrEngine();
#endif
            var text = await Task.Run(() => HardwarePdfTextReader.ReadAsync(_pdfPath, ocr, null, _cts.Token));
            _pages = text.Pages;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Status($"Couldn't read the PDF: {ex.Message}", AppColors.Danger);
            return;
        }

        if (_editing != null)
        {
            NameBox.Text = _editing.Name;
            RecognizeBox.Text = string.Join(Environment.NewLine, _editing.RecognizeBy);
            _headerLine = _editing.HeaderLine;
            HeadingBox.Text = _editing.HeadingPrefix;
            SkipBox.Text = string.Join(Environment.NewLine, _editing.SkipLinesStartingWith);
            CarryDownCheck.IsChecked = _editing.ManufacturerCarriesDown;
            MfrInCatalogCheck.IsChecked = _editing.ManufacturerInCatalog;
            JoinCheck.IsChecked = _editing.JoinWrappedDescriptions;
            QtyRequiredCheck.IsChecked = _editing.QuantityRequired;
            RebuildFrom(_editing.Columns);
        }
        else
        {
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(_pdfPath);
            RecognizeBox.Text = CustomPdfFormats.SuggestRecognizeText(Pages);
        }
        ShowPage(0);
        if (_editing != null) Preview();
    }

    private IReadOnlyList<IReadOnlyList<string>> Pages => _pages.Select(p => (IReadOnlyList<string>)p).ToList();

    private string? SelectedLine()
    {
        if (LinesList.SelectedItem is string s && !string.IsNullOrWhiteSpace(s)) return s;
        Status("Click a line on the left first.", AppColors.Danger);
        return null;
    }

    private void ShowPage(int page)
    {
        if (_pages.Count == 0) return;
        _page = Math.Clamp(page, 0, _pages.Count - 1);
        LinesList.ItemsSource = _pages[_page];
        PageLabel.Text = $"Page {_page + 1} of {_pages.Count}";
    }

    /// <summary>Rebuilds the column rows from the header/example lines (keeping fields already chosen where columns line up).</summary>
    private void Rebuild()
    {
        HeaderLineText.Text = _headerLine?.Trim() ?? "(none: columns are read by position)";
        ExampleLineText.Text = _exampleLine?.Trim() ?? "(choose one on the left)";
        if (_exampleLine == null && _headerLine == null) { RebuildFrom(new List<PdfFormatColumn>()); return; }
        var guess = CustomPdfFormats.GuessColumns(_exampleLine ?? _headerLine!, _headerLine);
        foreach (var g in guess)
        {
            var old = _columnRows.FirstOrDefault(r => Math.Abs(r.Column.Start - g.Start) <= 2);
            if (old.Combo?.SelectedItem is FieldItem f) g.Field = f.Field;
        }
        RebuildFrom(guess);
    }

    private void RebuildFrom(List<PdfFormatColumn> columns)
    {
        HeaderLineText.Text = _headerLine?.Trim() ?? "(none: columns are read by position)";
        ExampleLineText.Text = _exampleLine?.Trim() ?? (_editing != null ? "(choose one on the left to re-check the columns)" : "(choose one on the left)");
        _columnRows.Clear();
        ColumnsPanel.Children.Clear();
        var starts = columns.Select(c => c.Start).ToList();
        var exampleSlots = _exampleLine != null ? CustomPdfFormats.Split(_exampleLine, starts) : new string?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var combo = new ComboBox { ItemsSource = FieldItems, SelectedItem = FieldItems.First(f => f.Field == c.Field), Width = 190 };
            combo.SelectionChanged += (_, e) => { e.Handled = true; Preview(); };
            var label = c.HeaderLabel != null ? $"\"{c.HeaderLabel}\"" : $"col {c.Start}";
            var sample = exampleSlots[i] ?? string.Empty;
            var row = new DockPanel();
            DockPanel.SetDock(combo, Dock.Right);
            row.Children.Add(combo);
            row.Children.Add(new TextBlock
            {
                Text = sample.Length > 0 ? $"{label}:  {sample}" : label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
            });
            ColumnsPanel.Children.Add(row);
            _columnRows.Add((c, combo));
        }
        if (columns.Count > 0) Preview();
    }

    private CustomPdfFormat Draft() => new()
    {
        Name = (NameBox.Text ?? string.Empty).Trim(),
        RecognizeBy = (RecognizeBox.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        HeaderLine = _headerLine?.TrimEnd(),
        Columns = _columnRows.Select(r => new PdfFormatColumn
        {
            Start = r.Column.Start,
            HeaderLabel = r.Column.HeaderLabel,
            Field = (r.Combo.SelectedItem as FieldItem)?.Field ?? PdfFormatField.Ignore,
        }).ToList(),
        HeadingPrefix = string.IsNullOrWhiteSpace(HeadingBox.Text) ? null : HeadingBox.Text.Trim(),
        SkipLinesStartingWith = (SkipBox.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        ManufacturerCarriesDown = CarryDownCheck.IsChecked == true,
        ManufacturerInCatalog = MfrInCatalogCheck.IsChecked == true,
        JoinWrappedDescriptions = JoinCheck.IsChecked == true,
        QuantityRequired = QtyRequiredCheck.IsChecked == true,
        CreatedBy = _editing?.CreatedBy ?? SessionService.ActiveUserProfile?.UserName,
    };

    private HardwarePdfParseResult? Preview()
    {
        if (_pages.Count == 0) return null;
        var draft = Draft();
        if (draft.Columns.All(c => c.Field != PdfFormatField.Catalog))
        {
            PreviewHeader.Text = "Preview";
            PreviewList.ItemsSource = null;
            Status("Say which column is the model / catalog number.", AppColors.Warning);
            return null;
        }
        var result = CustomPdfFormats.Parse(draft, Pages);
        PreviewHeader.Text = $"Preview: {result.Lines.Count} item line{(result.Lines.Count == 1 ? "" : "s")} on {result.Lines.Select(l => l.Page).Distinct().Count()} page(s)";
        PreviewList.ItemsSource = result.Lines.Select(CustomPdfFormats.PreviewText).ToList();
        bool recognized = CustomPdfFormats.Matches(draft, Pages);
        Status(recognized
                ? "Check the preview. Change a column's choice, the heading text or the options until it's right."
                : "This PDF doesn't contain the \"recognize\" text (or the header line), so the format wouldn't be used for it.",
            recognized ? AppColors.Muted : AppColors.Warning);
        return result;
    }

    private async Task SaveAsync()
    {
        var draft = Draft();
        if (draft.Name.Length == 0) { Status("Give the format a name.", AppColors.Danger); return; }
        if (draft.Columns.All(c => c.Field != PdfFormatField.Catalog)) { Status("Say which column is the model / catalog number.", AppColors.Danger); return; }
        if (draft.RecognizeBy.Count == 0 && draft.HeaderLine == null)
        {
            Status("Enter text every PDF of this kind contains (e.g. its report title), or choose the column header line.", AppColors.Danger);
            return;
        }
        if (!CustomPdfFormats.Matches(draft, Pages)) { Status("This PDF doesn't contain the \"recognize\" text, so the format would never be used. Fix the text first.", AppColors.Danger); return; }
        var result = Preview();
        if (result == null || result.Lines.Count == 0) { Status("The format doesn't find any item lines yet.", AppColors.Danger); return; }

        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var existing = CustomPdfFormats.Load(ctx);
            bool clash = existing.Any(f => string.Equals(f.Name, draft.Name, StringComparison.OrdinalIgnoreCase)) &&
                         !string.Equals(_editing?.Name, draft.Name, StringComparison.OrdinalIgnoreCase);
            if (clash && !await DialogHelper.ConfirmAsync(this, $"There's already a format called \"{draft.Name}\". Replace it?", "Replace Format"))
                return;
            if (_editing != null && !string.Equals(_editing.Name, draft.Name, StringComparison.OrdinalIgnoreCase))
                CustomPdfFormats.Remove(ctx, _editing.Name);
            CustomPdfFormats.Upsert(ctx, draft);
        }
        catch (Exception ex)
        {
            Status($"Couldn't save: {ex.Message}", AppColors.Danger);
            return;
        }
        CloseWith(true);
    }

    private void Status(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }
}
