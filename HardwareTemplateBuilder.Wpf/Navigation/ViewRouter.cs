using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace HardwareTemplateBuilder.Wpf.Navigation;

/// <summary>
/// Simple navigation router that manages which view is displayed in the main content area.
/// Views are registered by name and a new instance is created on every navigate.
/// </summary>
public class ViewRouter
{
    /// <summary>The content control whose content is swapped during navigation.</summary>
    private readonly ContentControl _contentArea;

    /// <summary>Registry mapping view names to factory functions that create the view.</summary>
    private readonly Dictionary<string, Func<UserControl>> _viewFactories = new();

    /// <summary>Initializes a new <see cref="ViewRouter"/> bound to the given content area.</summary>
    public ViewRouter(ContentControl contentArea)
    {
        _contentArea = contentArea;
    }

    /// <summary>Registers a named view with a factory function.</summary>
    public void Register(string viewName, Func<UserControl> factory)
    {
        _viewFactories[viewName] = factory;
    }

    /// <summary>True if a view with this name has been registered.</summary>
    public bool IsRegistered(string viewName) => _viewFactories.ContainsKey(viewName);

    /// <summary>Navigates to the named view, replacing the current content.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the view name is not registered.</exception>
    public void NavigateTo(string viewName)
    {
        if (!_viewFactories.TryGetValue(viewName, out var factory))
            throw new InvalidOperationException($"View '{viewName}' is not registered.");
        _contentArea.Content = factory();
    }
}
