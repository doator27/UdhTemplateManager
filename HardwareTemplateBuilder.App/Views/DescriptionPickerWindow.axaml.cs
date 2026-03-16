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

    /// <summary>Required by the XAML compiler; use the parameterized constructor at runtime.</summary>
    public DescriptionPickerWindow() : this(System.Array.Empty<Description>()) { }

    /// <summary>
    /// Initializes the picker with the full flat description list.
    /// </summary>
    public DescriptionPickerWindow(IEnumerable<Description> allDescriptions)
    {
        _allDescs = allDescriptions.ToList();
        InitializeComponent();
        BuildTree();
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
        if (DescTree.SelectedItem is DescriptionNode node)
            Close((int?)node.Id);
    }

    private sealed class DescriptionNode
    {
        public int Id { get; set; }
        public string DescriptionText { get; set; } = "";
        public ObservableCollection<DescriptionNode> Children { get; } = new();
    }
}
