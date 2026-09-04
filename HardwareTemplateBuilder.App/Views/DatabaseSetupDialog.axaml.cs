using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.Core.Data;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Modal dialog used to configure the shared "master" database location that the local,
/// per-machine working copy syncs with on startup (see
/// <see cref="HardwareTemplateBuilder.Core.Data.MasterSyncService"/>).
/// <para>
/// The user must either create a new master database or connect to an existing one.
/// Once a valid path is confirmed, it is written to <see cref="DatabaseLocationService"/>
/// and the dialog closes. When <paramref name="required"/> is <c>true</c> the dialog cannot
/// be dismissed without making a valid selection; this is no longer required at startup since
/// the app always has a usable local working copy, but remains available for explicit
/// first-time setup flows.
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
    /// When non-null, an error banner is shown identifying the previously configured path
    /// that could not be reached.
    /// </param>
    /// <param name="required">
    /// When <c>true</c> (default) the dialog cannot be closed until a valid database path
    /// has been configured. Pass <c>false</c> when opening from the File menu to allow
    /// the user to cancel without changing the current location.
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
            ErrorPanel.IsVisible          = true;
            UnreachablePathLabel.Text     =
                $"The configured path could not be found:\n{_unreachablePath}\n\n" +
                "Create a new database or connect to a different one to continue.";
            HeaderLabel.Text = "Database Unreachable — Choose a New Location";
        }

        BrowseCreateButton.Click  += async (_, _) => await BrowseCreateFolderAsync();
        CreateButton.Click        += (_, _) => CreateDatabase();
        BrowseConnectButton.Click += async (_, _) => await BrowseConnectFileAsync();
        ConnectButton.Click       += (_, _) => ConnectDatabase();
    }

    /// <inheritdoc/>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_required && !_configured)
            e.Cancel = true;

        base.OnClosing(e);
    }

    // ---------- Create new ----------

    private async Task BrowseCreateFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Select Folder for New Database",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            CreateFolderBox.Text = folders[0].Path.LocalPath;
    }

    private void CreateDatabase()
    {
        CreateStatusLabel.Text = string.Empty;

        var folder = CreateFolderBox.Text?.Trim();
        if (string.IsNullOrEmpty(folder)) { CreateStatusLabel.Text = "Choose a folder first."; return; }
        if (!Directory.Exists(folder))    { CreateStatusLabel.Text = "Folder does not exist or is not accessible."; return; }

        var dbPath = Path.Combine(folder, "hardware_templates.db");

        try
        {
            // Initialise (or upgrade) the schema at the target path.
            DatabaseInitializer.InitializeAtPath(dbPath);
            DatabaseLocationService.SetConfiguredPath(dbPath);
            _configured = true;
            Close();
        }
        catch (Exception ex)
        {
            CreateStatusLabel.Text = $"Error creating database: {ex.Message}";
        }
    }

    // ---------- Connect to existing ----------

    private async Task BrowseConnectFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select Existing Database File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Database") { Patterns = new[] { "*.db" } }
            }
        });

        if (files.Count > 0)
            ConnectPathBox.Text = files[0].Path.LocalPath;
    }

    private void ConnectDatabase()
    {
        ConnectStatusLabel.Text = string.Empty;

        var path = ConnectPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path)) { ConnectStatusLabel.Text = "Choose a database file first."; return; }
        if (!File.Exists(path))         { ConnectStatusLabel.Text = "File not found or not accessible."; return; }

        try
        {
            // Apply any pending migrations (validates schema compatibility).
            DatabaseInitializer.InitializeAtPath(path);
            DatabaseLocationService.SetConfiguredPath(path);
            _configured = true;
            Close();
        }
        catch (Exception ex)
        {
            ConnectStatusLabel.Text = $"Cannot connect to database: {ex.Message}";
        }
    }
}
