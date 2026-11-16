using System;
using System.Windows;
using System.Windows.Controls;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// A <see cref="StackPanel"/> replacement with a <see cref="Spacing"/> gap between visible
/// children, matching Avalonia's <c>StackPanel.Spacing</c> (collapsed children take no gap).
/// </summary>
public class Stack : Panel
{
    /// <summary>Identifies the <see cref="Orientation"/> property.</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(Stack),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Identifies the <see cref="Spacing"/> property.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(Stack),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Gets or sets the stacking direction.</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Gets or sets the gap, in device-independent pixels, between visible children.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var childConstraint = horizontal
            ? new Size(double.PositiveInfinity, availableSize.Height)
            : new Size(availableSize.Width, double.PositiveInfinity);

        double along = 0, across = 0;
        var visibleCount = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child == null) continue;
            child.Measure(childConstraint);
            if (child.Visibility == Visibility.Collapsed) continue;
            visibleCount++;
            var size = child.DesiredSize;
            along += horizontal ? size.Width : size.Height;
            across = Math.Max(across, horizontal ? size.Height : size.Width);
        }

        if (visibleCount > 1) along += Spacing * (visibleCount - 1);
        return horizontal ? new Size(along, across) : new Size(across, along);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        double offset = 0;
        var first = true;
        foreach (UIElement child in InternalChildren)
        {
            if (child == null || child.Visibility == Visibility.Collapsed) continue;
            if (!first) offset += Spacing;
            first = false;

            var size = child.DesiredSize;
            if (horizontal)
            {
                child.Arrange(new Rect(offset, 0, size.Width, finalSize.Height));
                offset += size.Width;
            }
            else
            {
                child.Arrange(new Rect(0, offset, finalSize.Width, size.Height));
                offset += size.Height;
            }
        }

        return finalSize;
    }
}
