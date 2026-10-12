using System;
using Avalonia.Controls;
using HardwareTemplateBuilder.Core.Data;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Modal dialog used to configure the MySQL connection string that every client uses to connect
/// directly to the shared, centralized database (see <see cref="DatabaseInitializer"/>).
/// <para>
/// Once a valid connection string is confirmed, it is written to
/// <see cref="DatabaseLocationService"/> and the dialog closes. When <paramref name="required"/>
/// (constructor parameter) is <c>true</c>, the dialog cannot be dismissed without making a valid
/// selection.
/// </para>
/// </summary>
public partial class DatabaseSetupDialog : Window
{
    private readonly string? _unreachablePath;
    private readonly bool    _required;
    private bool _configured;

    /// <summary>Required by the Avalonia XAML compiler; delegates to the parameterized constructor.</summary>
    public DatabaseSetupDialog() : this(null, required: true) { }

    /// <summary>
    /// Initializes the dialog.
    /// </summary>
    /// <param name="unreachablePath">
    /// When non-null, an error banner is shown identifying the previously configured connection
    /// that could not be reached.
    /// </param>
    /// <param name="required">
    /// When <c>true</c> (default) the dialog cannot be closed until a valid connection string
    /// has been configured. Pass <c>false</c> when opening from the File menu to allow
    /// the user to cancel without changing the current configuration.
    /// </param>
    public DatabaseSetupDialog(string? unreachablePath, bool required = true)
    {
        _unreachablePath = unreachablePath;
        _required        = required;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        if (_unreachablePath != null)
        {
            ErrorPanel.IsVisible      = true;
            UnreachablePathLabel.Text =
                $"The configured connection could not be reached:\n{_unreachablePath}\n\n" +
                "Enter a new connection string to continue.";
            HeaderLabel.Text = "Database Unreachable — Enter a New Connection";
        }

        var existing = DatabaseLocationService.GetConfiguredConnectionString();
        if (!string.IsNullOrWhiteSpace(existing))
            ConnectionStringBox.Text = existing;

        ConnectButton.Click += (_, _) => Connect();
    }

    /// <inheritdoc/>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_required && !_configured)
            e.Cancel = true;

        base.OnClosing(e);
    }

    private void Connect()
    {
        ConnectStatusLabel.Text = string.Empty;

        var connectionString = ConnectionStringBox.Text?.Trim();
        if (string.IsNullOrEmpty(connectionString))
        {
            ConnectStatusLabel.Text = "Enter a connection string first.";
            return;
        }

        try
        {
            // Apply any pending migrations (validates connectivity and schema compatibility).
            DatabaseInitializer.InitializeAtConnectionString(connectionString);
            DatabaseLocationService.SetConfiguredConnectionString(connectionString);
            _configured = true;
            Close();
        }
        catch (Exception ex)
        {
            ConnectStatusLabel.Text = $"Cannot connect to database: {ex.Message}";
        }
    }
}
