using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// The busy (indeterminate) sweep of the Win95 progress bar (Themes/Win95Extras.xaml): moves the
/// block of the template's "Indeterminate" part from just off the left edge to just past the right
/// edge of the bar, whatever the bar's width, and restarts it when the bar is resized. The track
/// clips it so it never draws outside the bar. Registered once at startup (<see cref="Register"/>).
/// </summary>
public static class ProgressSweep
{
    private static readonly ConditionalWeakTable<FrameworkElement, object> Hooked = new();

    /// <summary>Hooks every progress bar as it loads.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ProgressBar), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Hook((ProgressBar)s)));

    private static void Hook(ProgressBar bar)
    {
        bar.ApplyTemplate();
        if (bar.Template?.FindName("Indeterminate", bar) is not FrameworkElement block ||
            bar.Template.FindName("PART_Track", bar) is not FrameworkElement track)
            return;
        if (!Hooked.TryGetValue(block, out _))
        {
            Hooked.Add(block, new object());
            // The template shows the block only while the bar is indeterminate (and visible).
            block.IsVisibleChanged += (_, _) => Update(block, track);
            track.SizeChanged += (_, _) => Update(block, track);
        }
        Update(block, track);
    }

    private static void Update(FrameworkElement block, FrameworkElement track)
    {
        if (block.RenderTransform is not TranslateTransform move || move.IsFrozen)
            block.RenderTransform = move = new TranslateTransform();
        double width = track.ActualWidth;
        double size = double.IsNaN(block.Width) ? 60 : block.Width;
        if (!block.IsVisible || width <= 0)
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.X = -size;
            return;
        }
        // About 300 pixels a second, so long and short bars sweep at the same speed.
        var duration = TimeSpan.FromSeconds(Math.Max(1.0, (width + size) / 300));
        move.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-size, width, duration) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
