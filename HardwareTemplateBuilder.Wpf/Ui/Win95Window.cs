using System.Threading.Tasks;
using System.Windows;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// Base class for the app's modal dialogs. Stands in for Avalonia's typed
/// <c>ShowDialog&lt;T&gt;</c>/<c>Close(result)</c>: the dialog calls <see cref="CloseWith"/>
/// and the caller awaits <see cref="ShowDialogAsync{T}"/> (default/null = cancelled).
/// Also applies the Win95 window look (gray face, MS Sans Serif, crisp text).
/// </summary>
public class Win95Window : Window
{
    private object? _result;

    /// <summary>Initializes the window with the shared Win95 window style.</summary>
    public Win95Window()
    {
        SetResourceReference(StyleProperty, "Win95Window");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
    }

    /// <summary>Closes the dialog and hands <paramref name="result"/> back to the awaiting caller.</summary>
    public void CloseWith(object? result)
    {
        _result = result;
        Close();
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/> and returns its result.</summary>
    public Task<T?> ShowDialogAsync<T>(Window? owner)
    {
        if (owner != null && owner != this && owner.IsLoaded) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowDialog();
        return Task.FromResult(_result is T typed ? typed : default);
    }

    /// <summary>Shows the dialog modally when no result is needed.</summary>
    public Task ShowDialogAsync(Window? owner) => ShowDialogAsync<object>(owner);
}
