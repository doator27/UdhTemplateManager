using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>Small helpers that keep ported code-behind close to the Avalonia original.</summary>
public static class UiExtensions
{
    /// <summary>Shows or collapses an element (Avalonia's <c>IsVisible = value</c>).</summary>
    public static void SetVisible(this UIElement element, bool visible)
        => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True when the element's own Visibility is Visible (Avalonia's <c>IsVisible</c> getter).</summary>
    public static bool IsShown(this UIElement element) => element.Visibility == Visibility.Visible;

    /// <summary>The window hosting this element, falling back to the app's main window.</summary>
    public static Window? OwnerWindow(this DependencyObject element)
        => Window.GetWindow(element) ?? Application.Current?.MainWindow;

    /// <summary>Walks up the visual tree to the nearest ancestor of type <typeparamref name="T"/>.</summary>
    public static T? FindAncestor<T>(this DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T match) return match;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>Opens a file, folder or URL with the shell's default handler.</summary>
    public static void OpenWithShell(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>Parses a "#RRGGBB" hex string into a frozen brush.</summary>
    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Calls <paramref name="handler"/> whenever <paramref name="property"/> changes on
    /// <paramref name="target"/> (Avalonia's <c>PropertyChanged</c> + <c>e.Property ==</c> check).
    /// </summary>
    public static void WhenChanged<T>(this T target, DependencyProperty property, Action handler) where T : DependencyObject
    {
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, typeof(T));
        descriptor?.AddValueChanged(target, (_, _) => handler());
    }

    /// <summary>Sets the Win95 placeholder text and returns the control, for use in initializer chains.</summary>
    public static T WithWatermark<T>(this T control, string text) where T : DependencyObject
    {
        Watermark.SetText(control, text);
        return control;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread without waiting (Avalonia's <c>Dispatcher.UIThread.Post</c>).</summary>
    public static void Post(Action action)
        => Application.Current.Dispatcher.BeginInvoke(action);
}
