using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HardwareTemplateBuilder.Wpf.Ui;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>What the user chose in the <see cref="AssemblyWindow"/>.</summary>
/// <param name="Parts">The parts (empty = not an assembly).</param>
/// <param name="Description">The description that starts the label.</param>
public sealed record AssemblyChoice(List<HardwareImportGroup> Parts, string Description);

/// <summary>
/// Parse PDF into Bulk Add: makes a callout an assembly. Lists the other callouts in the same
/// heading/set (or every heading) with tick boxes, takes a description, and shows the label the
/// assembly line will get (description, then each callout, joined with " | ",
/// <see cref="HardwareImportAssemblies.Label"/>). Returns an <see cref="AssemblyChoice"/>
/// (no parts = "Not an Assembly"), or null when cancelled.
/// </summary>
public partial class AssemblyWindow : Win95Window
{
    private readonly HardwareImportSession? _session;
    private readonly HardwareImportGroup? _anchor;
    private readonly HashSet<HardwareImportGroup> _ticked = new();

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public AssemblyWindow() => InitializeComponent();

    /// <summary>Creates the window.</summary>
    /// <param name="session">The import session.</param>
    /// <param name="anchor">The callout that is the assembly.</param>
    public AssemblyWindow(HardwareImportSession session, HardwareImportGroup anchor)
    {
        _session = session;
        _anchor = anchor;
        InitializeComponent();

        bool suggested = !anchor.IsAssembly && anchor.SuggestedAssemblyParts.Count > 0;
        foreach (var p in anchor.IsAssembly ? anchor.AssemblyParts : anchor.SuggestedAssemblyParts) _ticked.Add(p);
        DescriptionBox.Text = anchor.IsAssembly ? anchor.AssemblyDescription
            : suggested ? anchor.SuggestedAssemblyDescription ?? string.Empty
            : anchor.GenericName;
        var sets = anchor.Sets;
        HeaderText.Text = $"Assembly: {anchor.GenericName}  {anchor.Catalog}";
        IntroText.Text =
            (sets.Count > 0 ? $"Items in {string.Join(", ", sets.Take(3))}{(sets.Count > 3 ? ", ..." : string.Empty)}. " : string.Empty) +
            "The ticked items become one line: this item, labelled with the description and every callout joined with |. " +
            "They aren't added on their own. After this, choose the hardware item the assembly is on the wizard page." +
            (suggested ? "\nLast time this item was an assembly with the ticked items." : string.Empty);
        RemoveButton.SetVisible(anchor.IsAssembly);
        AllHeadingsCheck.IsChecked = _ticked.Any(p => !HardwareImportAssemblies.Candidates(anchor, session.Analysis.Groups, false).Contains(p));

        AllHeadingsCheck.Click += (_, _) => BuildList();
        DescriptionBox.TextChanged += (_, _) => UpdatePreview();
        OkButton.Click += (_, _) =>
        {
            if (_ticked.Count == 0)
            {
                StatusLabel.Text = "Tick at least one item, or use Not an Assembly.";
                return;
            }
            CloseWith(Result());
        };
        RemoveButton.Click += (_, _) => CloseWith(new AssemblyChoice(new List<HardwareImportGroup>(), string.Empty));
        CancelButton.Click += (_, _) => CloseWith(null);
        BuildList();
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
            box.Click += (_, _) =>
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

    private AssemblyChoice Result() =>
        new(_session!.Analysis.Groups.Where(_ticked.Contains).ToList(), (DescriptionBox.Text ?? string.Empty).Trim());

    private void UpdatePreview()
    {
        if (_anchor == null || _session == null) return;
        var r = Result();
        LabelPreview.Text = r.Parts.Count == 0 ? "(tick the items that go with it)" : HardwareImportAssemblies.Label(r.Description, _anchor, r.Parts);
        StatusLabel.Text = string.Empty;
    }
}
