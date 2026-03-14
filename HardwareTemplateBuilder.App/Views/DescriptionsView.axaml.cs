using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// View for managing the <see cref="Description"/> hierarchy tree.
/// Users can add root items, add children to any node, rename, reorder within siblings,
/// indent/unindent, and delete leaf nodes.
/// </summary>
public partial class DescriptionsView : UserControl
{
    /// <summary>
    /// Lightweight tree node used as the data item for the <c>TreeView</c>.
    /// </summary>
    private sealed class DescriptionNode
    {
        public int Id { get; init; }
        public string DescriptionText { get; set; } = "";
        public int SortOrder { get; set; }
        public int? ParentId { get; set; }
        public ObservableCollection<DescriptionNode> Children { get; } = new();
    }

    private DescriptionRepository? _repo;
    /// <summary>Root nodes of the currently displayed tree.</summary>
    private readonly ObservableCollection<DescriptionNode> _roots = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public DescriptionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new DescriptionRepository(context);

        DescTree.ItemsSource = _roots;

        MainMenuButton.Click   += (_, _) => NavigationRequested?.Invoke("Dashboard");
        DescTree.SelectionChanged += (_, _) => OnTreeSelectionChanged();
        SaveButton.Click       += (_, _) => Save();
        AddRootButton.Click    += (_, _) => AddNode(parentId: null);
        AddChildButton.Click   += (_, _) => AddChildToSelected();
        DeleteButton.Click     += (_, _) => DeleteSelected();
        MoveUpButton.Click     += (_, _) => MoveSelected(-1);
        MoveDownButton.Click   += (_, _) => MoveSelected(1);
        IndentButton.Click     += (_, _) => IndentSelected();
        UnindentButton.Click   += (_, _) => UnindentSelected();

        LoadTree();
    }

    // ── Tree build ────────────────────────────────────────────────────────

    private void LoadTree()
    {
        var all = _repo!.GetAll().ToList();
        _roots.Clear();
        BuildNodes(all, null, _roots);
    }

    private static void BuildNodes(
        List<Description> all,
        int? parentId,
        ObservableCollection<DescriptionNode> target)
    {
        var children = all
            .Where(d => d.ParentId == parentId)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText);

        foreach (var d in children)
        {
            var node = new DescriptionNode
            {
                Id = d.Id, DescriptionText = d.DescriptionText,
                SortOrder = d.SortOrder, ParentId = d.ParentId
            };
            BuildNodes(all, d.Id, node.Children);
            target.Add(node);
        }
    }

    private DescriptionNode? SelectedNode =>
        DescTree.SelectedItem as DescriptionNode;

    private void OnTreeSelectionChanged()
    {
        if (SelectedNode is DescriptionNode node)
        {
            DescriptionBox.Text = node.DescriptionText;
            StatusLabel.Text = "";
        }
    }

    // ── CRUD ──────────────────────────────────────────────────────────────

    private void Save()
    {
        var node = SelectedNode;
        if (node == null) { StatusLabel.Text = "Select a node to rename."; return; }

        var text = DescriptionBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { StatusLabel.Text = "Name is required."; return; }

        var existing = _repo!.GetById(node.Id);
        if (existing == null) return;
        existing.DescriptionText = text;
        _repo.Update(existing);
        StatusLabel.Text = "Saved.";
        LoadTree();
    }

    private void AddNode(int? parentId)
    {
        var text = DescriptionBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { StatusLabel.Text = "Enter a name first."; return; }

        using var ctx = DatabaseInitializer.CreateContext();
        var siblings = ctx.Descriptions.Where(d => d.ParentId == parentId).ToList();
        var maxOrder = siblings.Any() ? siblings.Max(d => d.SortOrder) : -1;

        _repo!.Add(new Description
        {
            DescriptionText = text,
            ParentId = parentId,
            SortOrder = maxOrder + 1
        });

        StatusLabel.Text = "Added.";
        DescriptionBox.Text = "";
        LoadTree();
    }

    private void AddChildToSelected()
    {
        var node = SelectedNode;
        if (node == null) { StatusLabel.Text = "Select a parent node first."; return; }
        AddNode(parentId: node.Id);
    }

    private void DeleteSelected()
    {
        var node = SelectedNode;
        if (node == null) { StatusLabel.Text = "Select a node to delete."; return; }
        if (node.Children.Count > 0)
        {
            StatusLabel.Text = "Remove all children before deleting this item.";
            return;
        }

        using var ctx = DatabaseInitializer.CreateContext();
        var entity = ctx.Descriptions.Find(node.Id);
        if (entity != null)
        {
            ctx.Descriptions.Remove(entity);
            ctx.SaveChanges();
        }
        StatusLabel.Text = "Deleted.";
        DescriptionBox.Text = "";
        LoadTree();
    }

    // ── Reorder ───────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the selected node up (-1) or down (+1) within its sibling list.
    /// </summary>
    private void MoveSelected(int direction)
    {
        var node = SelectedNode;
        if (node == null) return;

        using var ctx = DatabaseInitializer.CreateContext();
        var siblings = ctx.Descriptions
            .Where(d => d.ParentId == node.ParentId)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText)
            .ToList();

        // Normalise to 0,1,2,…
        for (int i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;

        var idx = siblings.FindIndex(d => d.Id == node.Id);
        if (idx < 0) return;
        var swapIdx = idx + direction;
        if (swapIdx < 0 || swapIdx >= siblings.Count) return;

        (siblings[idx].SortOrder, siblings[swapIdx].SortOrder) =
            (siblings[swapIdx].SortOrder, siblings[idx].SortOrder);

        ctx.SaveChanges();
        LoadTree();
    }

    /// <summary>
    /// Makes the selected node a child of its immediately preceding sibling
    /// (moves it one level deeper).
    /// </summary>
    private void IndentSelected()
    {
        var node = SelectedNode;
        if (node == null) return;

        using var ctx = DatabaseInitializer.CreateContext();
        var siblings = ctx.Descriptions
            .Where(d => d.ParentId == node.ParentId)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText)
            .ToList();

        var idx = siblings.FindIndex(d => d.Id == node.Id);
        if (idx <= 0) { StatusLabel.Text = "No sibling above to indent under."; return; }

        var newParent = siblings[idx - 1];
        var newSiblings = ctx.Descriptions.Where(d => d.ParentId == newParent.Id).ToList();
        var maxOrder = newSiblings.Any() ? newSiblings.Max(d => d.SortOrder) : -1;

        var entity = ctx.Descriptions.Find(node.Id)!;
        entity.ParentId = newParent.Id;
        entity.SortOrder = maxOrder + 1;
        ctx.SaveChanges();
        LoadTree();
    }

    /// <summary>
    /// Moves the selected node up one level (makes it a sibling of its current parent,
    /// placed immediately after it).
    /// </summary>
    private void UnindentSelected()
    {
        var node = SelectedNode;
        if (node == null) return;
        if (node.ParentId == null) { StatusLabel.Text = "Already a root item."; return; }

        using var ctx = DatabaseInitializer.CreateContext();
        var parent = ctx.Descriptions.Find(node.ParentId.Value);
        if (parent == null) return;

        // Place after parent among grandparent's children.
        var grandSiblings = ctx.Descriptions
            .Where(d => d.ParentId == parent.ParentId)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText)
            .ToList();

        // Normalise, then insert after parent.
        for (int i = 0; i < grandSiblings.Count; i++) grandSiblings[i].SortOrder = i * 2;
        var parentInList = grandSiblings.FirstOrDefault(d => d.Id == parent.Id);
        if (parentInList != null)
        {
            var entity = ctx.Descriptions.Find(node.Id)!;
            entity.ParentId = parent.ParentId;
            entity.SortOrder = parentInList.SortOrder + 1;

            // Re-normalise to close the gap
            foreach (var s in grandSiblings.Where(d => d.SortOrder > parentInList.SortOrder))
                s.SortOrder += 2;
        }
        ctx.SaveChanges();
        LoadTree();
    }
}
