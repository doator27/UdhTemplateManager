using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.Wpf.Helpers;

/// <summary>
/// Type-to-jump for every list box, drop-down and tree in the app: with one focused, typing
/// letters ("vo") selects and scrolls to the first item whose text starts with them ("Von Duprin");
/// the same letter again moves to the next one. Typing into a text box (or an editable drop-down)
/// is left alone. Replaces WPF's own text search, which only knows an item's ToString. Registered
/// once at startup (<see cref="Register"/>); matching is <see cref="TypeAheadSearch"/>.
/// </summary>
public static class TypeToJump
{
    private static readonly ConditionalWeakTable<Control, TypeAheadSearch> Searches = new();
    private static readonly ConditionalWeakTable<Control, StrongBox<DateTime>> TypedAt = new();

    /// <summary>Hooks typing in all list boxes, combo boxes and tree views.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ListBox), UIElement.PreviewTextInputEvent, new TextCompositionEventHandler((s, e) => OnList((Selector)s, e)));
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewTextInputEvent, new TextCompositionEventHandler((s, e) => OnList((Selector)s, e)));
        EventManager.RegisterClassHandler(typeof(TreeView), UIElement.PreviewTextInputEvent, new TextCompositionEventHandler((s, e) => OnTree((TreeView)s, e)));
    }

    private static DateTime LastTyped(Control owner) => TypedAt.TryGetValue(owner, out var t) ? t.Value : DateTime.MinValue;

    /// <summary>The search text so far for a control, or null when the typing isn't for it.</summary>
    private static string? Search(Control owner, TextCompositionEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return null;
        if (owner is ComboBox { IsEditable: true }) return null;
        for (var v = e.OriginalSource as DependencyObject; v != null && v != owner; v = Parent(v))
            if (v is TextBoxBase) return null;
        // A space on its own toggles ticks/selection in lists: only use it inside a search.
        if (string.IsNullOrWhiteSpace(e.Text) && (DateTime.Now - LastTyped(owner)) > TypeAheadSearch.ResetAfter) return null;
        TypedAt.GetValue(owner, _ => new StrongBox<DateTime>()).Value = DateTime.Now;
        return Searches.GetValue(owner, _ => new TypeAheadSearch()).Append(e.Text, DateTime.Now);
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static void OnList(Selector list, TextCompositionEventArgs e)
    {
        var search = Search(list, e);
        if (search == null) return;
        var path = !string.IsNullOrEmpty(TextSearch.GetTextPath(list)) ? TextSearch.GetTextPath(list) : list.DisplayMemberPath;
        var items = list.Items.Cast<object?>().ToList();
        var texts = items.Select(i => TypeAheadSearch.ItemText(i, path)).ToList();
        int i = TypeAheadSearch.Find(texts, list.SelectedIndex, search);
        if (i < 0) return;
        e.Handled = true;

        if (list is ListBox lb)
        {
            if (lb.SelectionMode != SelectionMode.Single) lb.UnselectAll();
            lb.SelectedIndex = i;
            lb.ScrollIntoView(items[i]);
            lb.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => (lb.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)?.Focus()));
        }
        else
        {
            list.SelectedIndex = i;
            if (list is ComboBox { IsDropDownOpen: true } cb)
                (cb.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)?.BringIntoView();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int k = 0; k < VisualTreeHelper.GetChildrenCount(root); k++)
        {
            var child = VisualTreeHelper.GetChild(root, k);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static void OnTree(TreeView tree, TextCompositionEventArgs e)
    {
        var search = Search(tree, e);
        if (search == null) return;
        // The nodes showing now (collapsed branches' children aren't visible), top to bottom.
        var nodes = Descendants<TreeViewItem>(tree).Where(t => t.IsVisible).ToList();
        var texts = nodes.Select(t => TypeAheadSearch.ItemText(t.DataContext ?? t.Header) ?? t.Header?.ToString()).ToList();
        int i = TypeAheadSearch.Find(texts, nodes.FindIndex(t => t.IsSelected), search);
        if (i < 0) return;
        e.Handled = true;
        var node = nodes[i];
        node.IsSelected = true;
        node.BringIntoView();
        node.Focus();
    }
}
