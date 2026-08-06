using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Modal window that displays the full description tree and returns the ID of the
/// selected <see cref="Description"/>. Open via <c>ShowDialog&lt;int?&gt;</c>;
/// returns <c>null</c> when cancelled.
/// </summary>
public partial class DescriptionPickerWindow : Window
{
    private readonly List<Description> _allDescs;
    private readonly bool _leafOnly;
    private readonly bool _allowTopLevel;
    private readonly string _topLevelLabel;

    /// <summary>Sentinel ID returned when the synthetic "top level" node is selected.</summary>
    public const int TopLevelId = 0;

    /// <summary>Required by the XAML compiler; use the parameterized constructor at runtime.</summary>
    public DescriptionPickerWindow() : this(System.Array.Empty<Description>()) { }

    /// <summary>
    /// Initializes the picker with the full flat description list.
    /// </summary>
    /// <param name="allDescriptions">Every description, used to build the full tree for navigation context.</param>
    /// <param name="leafOnly">
    /// When true, the full tree is still shown for navigation, but only a node with no
    /// children (a leaf) can actually be selected/committed — used when picking a description
    /// to categorize a hardware item, where an intermediate parent category isn't meaningful.
    /// </param>
    /// <param name="allowTopLevel">
    /// When true, a synthetic "(Top Level)" node (<see cref="TopLevelId"/>) is added as a
    /// selectable sibling of the real roots — used when choosing a parent for a brand-new
    /// description, so the user can explicitly pick "no parent" instead of only cancelling.
    /// </param>
    /// <param name="topLevelLabel">Display text for the synthetic top-level node.</param>
    public DescriptionPickerWindow(
        IEnumerable<Description> allDescriptions,
        bool leafOnly = false,
        bool allowTopLevel = false,
        string topLevelLabel = "(Top Level — no parent)")
    {
        _allDescs      = allDescriptions.ToList();
        _leafOnly      = leafOnly;
        _allowTopLevel = allowTopLevel;
        _topLevelLabel = topLevelLabel;
        InitializeComponent();
        BuildTree();
        if (_leafOnly)
            InstructionLabel.Text = "Double-click or select a lowest-level item and click Select:";
        SelectButton.Click   += (_, _) => CommitSelection();
        CancelButton.Click   += (_, _) => Close(null);
        DescTree.DoubleTapped += (_, _) => CommitSelection();
    }

    private void BuildTree()
    {
        var roots = _allDescs
            .Where(d => d.ParentId == null)
            .OrderBy(d => d.SortOrder)
            .Select(BuildNode)
            .ToList();

        if (_allowTopLevel)
            roots.Insert(0, new DescriptionNode { Id = TopLevelId, DescriptionText = _topLevelLabel });

        DescTree.ItemsSource = roots;
    }

    private DescriptionNode BuildNode(Description d)
    {
        var node = new DescriptionNode { Id = d.Id, DescriptionText = d.DescriptionText };
        foreach (var child in _allDescs.Where(c => c.ParentId == d.Id).OrderBy(c => c.SortOrder))
            node.Children.Add(BuildNode(child));
        return node;
    }

    private void CommitSelection()
    {
        if (DescTree.SelectedItem is not DescriptionNode node) return;

        if (_leafOnly && node.Id != TopLevelId && node.Children.Count > 0)
        {
            StatusLabel.Text = "Please select a lowest-level item — this one has sub-items.";
            return;
        }

        Close((int?)node.Id);
    }

    private sealed class DescriptionNode
    {
        public int Id { get; set; }
        public string DescriptionText { get; set; } = "";
        public ObservableCollection<DescriptionNode> Children { get; } = new();
    }
}
