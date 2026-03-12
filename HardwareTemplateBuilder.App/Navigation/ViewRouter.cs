using Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace HardwareTemplateBuilder.App.Navigation;

/// <summary>
/// Simple navigation router that manages which view is displayed in the main content area.
/// Views are registered by name and swapped in/out by calling <see cref="NavigateTo"/>.
/// </summary>
public class ViewRouter
{
    /// <summary>The content control whose content is swapped during navigation.</summary>
    private readonly ContentControl _contentArea;

    /// <summary>Registry mapping view names to factory functions that create the view.</summary>
    private readonly Dictionary<string, Func<UserControl>> _viewFactories = new();

    /// <summary>Initializes a new <see cref="ViewRouter"/> bound to the given content area.</summary>
    /// <param name="contentArea">The <see cref="ContentControl"/> that hosts the active view.</param>
    public ViewRouter(ContentControl contentArea)
    {
        _contentArea = contentArea;
    }

    /// <summary>
    /// Registers a named view with a factory function.
    /// </summary>
    /// <param name="viewName">The unique name for this view (used to navigate to it).</param>
    /// <param name="factory">A function that creates a new instance of the view.</param>
    public void Register(string viewName, Func<UserControl> factory)
    {
        _viewFactories[viewName] = factory;
    }

    /// <summary>
    /// Navigates to the named view, replacing the current content.
    /// </summary>
    /// <param name="viewName">The name of the view to display.</param>
    /// <exception cref="InvalidOperationException">Thrown if the view name is not registered.</exception>
    public void NavigateTo(string viewName)
    {
        if (!_viewFactories.TryGetValue(viewName, out var factory))
            throw new InvalidOperationException($"View '{viewName}' is not registered.");
        _contentArea.Content = factory();
    }
}
