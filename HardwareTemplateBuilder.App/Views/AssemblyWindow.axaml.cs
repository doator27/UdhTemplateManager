using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>What the user chose in the <see cref="AssemblyWindow"/>.</summary>
/// <param name="Parts">The parts (empty = not an assembly).</param>
/// <param name="Description">The description text that starts the label.</param>
/// <param name="DescriptionId">The description (category) chosen from the tree, if any.</param>
public sealed record AssemblyChoice(List<HardwareImportGroup> Parts, string Description, int? DescriptionId = null);

/// <summary>
/// Parse PDF into Bulk Add: makes a callout an assembly. Lists the other callouts in the same
/// heading/set (or every heading) with tick boxes, takes a description from the Description tree, and shows the label the
/// assembly line will get (description, then each callout, joined with " | ",
/// <see cref="HardwareImportAssemblies.Label"/>). Returns an <see cref="AssemblyChoice"/>
/// (no parts = "Not an Assembly"), or null when cancelled.
/// </summary>
public partial class AssemblyWindow : Window
{
    private readonly HardwareImportSession? _session;
    private readonly HardwareImportGroup? _anchor;
    private readonly HashSet<HardwareImportGroup> _ticked = new();
    private int? _descriptionId;
    private string _descriptionText = string.Empty;

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public AssemblyWindow() => InitializeComponent();

    /// <summary>Creates the window.</summary>
    /// <param name="session">The import session.</param>
    /// <param name="anchor">The callout that is the assembly.</param>
    /// <param name="pdfPath">The PDF being imported, for the heading preview (null = no preview).</param>
    public AssemblyWindow(HardwareImportSession session, HardwareImportGroup anchor, string? pdfPath = null)
    {
        _session = session;
        _anchor = anchor;
        InitializeComponent();

        bool suggested = !anchor.IsAssembly && anchor.SuggestedAssemblyParts.Count > 0;
        foreach (var p in anchor.IsAssembly ? anchor.AssemblyParts : anchor.SuggestedAssemblyParts) _ticked.Add(p);
        if (anchor.IsAssembly) SetDescription(anchor.AssemblyDescriptionId, anchor.AssemblyDescription);
        else if (suggested) SetDescription(anchor.SuggestedAssemblyDescriptionId, anchor.SuggestedAssemblyDescription ?? string.Empty);
        else
        {
            int? id = anchor.CreateNew ? anchor.NewDescriptionId : session.Item(anchor.HardwareItemId)?.DescriptionId ?? anchor.NewDescriptionId;
            SetDescription(id, string.Empty);
        }
        var sets = anchor.Sets;
        HeaderText.Text = $"Assembly: {anchor.GenericName}  {anchor.Catalog}";
        IntroText.Text =
            (sets.Count > 0 ? $"Items in {string.Join(", ", sets.Take(3))}{(sets.Count > 3 ? ", ..." : string.Empty)}. " : string.Empty) +
            "The ticked items become one line: this item, labelled with the description and every callout joined with |. " +
            "They aren't added on their own. After this, choose the hardware item the assembly is on the wizard page." +
            (suggested ? "\nLast time this item was an assembly with the ticked items." : string.Empty);
        RemoveButton.IsVisible = anchor.IsAssembly;
        AllHeadingsCheck.IsChecked = _ticked.Any(p => !HardwareImportAssemblies.Candidates(anchor, session.Analysis.Groups, false).Contains(p));

        AllHeadingsCheck.IsCheckedChanged += (_, _) => BuildList();
        ChooseDescriptionButton.Click += async (_, _) =>
        {
            var picker = new DescriptionPickerWindow(session.Descriptions.Where(d => d.IsActive), leafOnly: false);
            if (await picker.ShowDialog<int?>(this) is int picked) SetDescription(picked, string.Empty);
        };
        ClearDescriptionButton.Click += (_, _) => SetDescription(null, string.Empty);
        OkButton.Click += (_, _) =>
        {
            if (_ticked.Count == 0)
            {
                StatusLabel.Text = "Tick at least one item, or use Not an Assembly.";
                return;
            }
            Close(Result());
        };
        RemoveButton.Click += (_, _) => Close(new AssemblyChoice(new List<HardwareImportGroup>(), string.Empty));
        CancelButton.Click += (_, _) => Close(null);
        BuildList();
        Opened += async (_, _) => await ShowHeadingAsync(pdfPath);
    }

    /// <summary>Shows the PDF page(s) the callout's heading is on, so the whole heading can be read.</summary>
    private async System.Threading.Tasks.Task ShowHeadingAsync(string? pdfPath)
    {
        if (_session == null || _anchor == null) return;
        var pages = HardwareImportAssemblies.HeadingPages(_anchor, _session.Analysis.Groups);
        var heading = _anchor.Sets.Count > 0 ? string.Join(", ", _anchor.Sets.Take(2)) : "This item";
        if (string.IsNullOrEmpty(pdfPath) || pages.Count == 0 || !System.IO.File.Exists(pdfPath))
        {
            HeadingPreviewText.Text = "The PDF page isn't available to preview.";
            await HeadingPreview.ShowPdfAsync(null);
            return;
        }
        HeadingPreviewText.Text = $"{heading}: page{(pages.Count == 1 ? "" : "s")} {string.Join(", ", pages)} of the PDF";
        try
        {
            var bytes = await System.Threading.Tasks.Task.Run(() => HardwarePdfImportService.PagesPdf(pdfPath, pages));
            await HeadingPreview.ShowPdfAsync(bytes);
        }
        catch (System.Exception ex)
        {
            HeadingPreviewText.Text = $"Couldn't show the PDF page: {ex.Message}";
        }
    }

    private void BuildList()
    {
        if (_session == null || _anchor == null) return;
        PartsList.Children.Clear();
        var candidates = HardwareImportAssemblies.Candidates(_anchor, _session.Analysis.Groups, AllHeadingsCheck.IsChecked == true);
        foreach (var g in candidates)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = _session.ClusterLine(g), TextWrapping = TextWrapping.Wrap },
                IsChecked = _ticked.Contains(g),
                Tag = g,
            };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _ticked.Add(g); else _ticked.Remove(g);
                UpdatePreview();
            };
            PartsList.Children.Add(box);
        }
        if (candidates.Count == 0)
            PartsList.Children.Add(new TextBlock { Text = "No other items in this heading. Tick \"Show items from every heading\".", TextWrapping = TextWrapping.Wrap });
        UpdatePreview();
    }

    /// <summary>Sets the description: from the tree when <paramref name="id"/> is known, else the old text.</summary>
    private void SetDescription(int? id, string fallbackText)
    {
        var d = id == null ? null : _session?.Descriptions.FirstOrDefault(x => x.Id == id);
        _descriptionId = d?.Id;
        _descriptionText = d?.DescriptionText ?? fallbackText.Trim();
        DescriptionBox.Text = d != null ? _session!.DescriptionPath(d.Id) : _descriptionText;
        UpdatePreview();
    }

    private AssemblyChoice Result() =>
        new(_session!.Analysis.Groups.Where(_ticked.Contains).ToList(), _descriptionText, _descriptionId);

    private void UpdatePreview()
    {
        if (_anchor == null || _session == null) return;
        var r = Result();
        LabelPreview.Text = r.Parts.Count == 0 ? "(tick the items that go with it)" : HardwareImportAssemblies.Label(r.Description, _anchor, r.Parts);
        StatusLabel.Text = string.Empty;
    }
}
