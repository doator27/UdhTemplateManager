using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Type-to-jump for every list box, drop-down and tree in the app: with one focused, typing
/// letters ("vo") selects and scrolls to the first item whose text starts with them ("Von Duprin");
/// the same letter again moves to the next one. Typing into a text box (or an editable drop-down)
/// is left alone. Registered once at startup (<see cref="Register"/>); matching is
/// <see cref="TypeAheadSearch"/>.
/// </summary>
public static class TypeToJump
{
    private static readonly ConditionalWeakTable<Control, TypeAheadSearch> Searches = new();

    /// <summary>Hooks typing in all list boxes, combo boxes and tree views.</summary>
    public static void Register()
    {
        InputElement.TextInputEvent.AddClassHandler<ListBox>((c, e) => OnList(c, e), RoutingStrategies.Tunnel);
        InputElement.TextInputEvent.AddClassHandler<ComboBox>((c, e) => OnList(c, e), RoutingStrategies.Tunnel);
        InputElement.TextInputEvent.AddClassHandler<TreeView>((c, e) => OnTree(c, e), RoutingStrategies.Tunnel);
    }

    /// <summary>The search text so far for a control, or null when the typing isn't for it.</summary>
    private static string? Search(Control owner, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return null;
        if (owner is ComboBox { IsEditable: true }) return null;
        for (var v = e.Source as Visual; v != null && v != owner; v = v.GetVisualParent())
            if (v is TextBox) return null;
        var search = Searches.GetValue(owner, _ => new TypeAheadSearch());
        // A space on its own toggles ticks/selection in lists: only use it inside a search.
        if (string.IsNullOrWhiteSpace(e.Text) && (DateTime.Now - LastTyped(owner)) > TypeAheadSearch.ResetAfter) return null;
        TypedAt.GetValue(owner, _ => new StrongBox<DateTime>()).Value = DateTime.Now;
        return search.Append(e.Text, DateTime.Now);
    }

    private static readonly ConditionalWeakTable<Control, StrongBox<DateTime>> TypedAt = new();

    private static DateTime LastTyped(Control owner) => TypedAt.TryGetValue(owner, out var t) ? t.Value : DateTime.MinValue;

    private static void OnList(SelectingItemsControl list, TextInputEventArgs e)
    {
        var search = Search(list, e);
        if (search == null) return;
        var path = (list.DisplayMemberBinding as Binding)?.Path;
        var texts = list.Items.Cast<object?>().Select(i => TypeAheadSearch.ItemText(i, path)).ToList();
        int i = TypeAheadSearch.Find(texts, list.SelectedIndex, search);
        if (i < 0) return;
        e.Handled = true;

        if (list is ListBox lb && lb.SelectionMode.HasFlag(SelectionMode.Multiple))
        {
            lb.Selection.Clear();
            lb.Selection.Select(i);
        }
        else
        {
            list.SelectedIndex = i;
        }
        list.ScrollIntoView(i);
        if (list is ListBox box)
            Dispatcher.UIThread.Post(() => (box.ContainerFromIndex(i) as Control)?.Focus(), DispatcherPriority.Background);
    }

    private static void OnTree(TreeView tree, TextInputEventArgs e)
    {
        var search = Search(tree, e);
        if (search == null) return;
        // The nodes showing now (collapsed branches' children aren't visible), top to bottom.
        var nodes = tree.GetVisualDescendants().OfType<TreeViewItem>().Where(t => t.IsEffectivelyVisible).ToList();
        var texts = nodes.Select(t => TypeAheadSearch.ItemText(t.DataContext ?? t.Header) ?? t.Header?.ToString()).ToList();
        int i = TypeAheadSearch.Find(texts, nodes.FindIndex(t => t.IsSelected), search);
        if (i < 0) return;
        e.Handled = true;
        var node = nodes[i];
        tree.SelectedItem = node.DataContext ?? node;
        node.BringIntoView();
        node.Focus();
    }
}
