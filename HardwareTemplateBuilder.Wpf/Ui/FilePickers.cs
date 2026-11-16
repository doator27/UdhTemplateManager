using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// Wrappers over the Win32 common file dialogs that return plain paths (null = cancelled),
/// standing in for Avalonia's <c>StorageProvider</c> pickers.
/// </summary>
public static class FilePickers
{
    /// <summary>File filter for SQLite database files.</summary>
    public const string SqliteFilter = "SQLite Database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*";

    /// <summary>File filter for PDF files.</summary>
    public const string PdfFilter = "PDF Files (*.pdf)|*.pdf";

    /// <summary>Shows an Open dialog for a single file.</summary>
    public static string? OpenFile(Window? owner, string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Shows an Open dialog and returns the chosen paths (empty when cancelled), so ported
    /// <c>files.Count</c> / <c>files[0]</c> code keeps working.
    /// </summary>
    public static List<string> OpenFiles(Window? owner, string title, string filter, bool allowMultiple = false)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = allowMultiple };
        return dlg.ShowDialog(owner) == true ? dlg.FileNames.ToList() : new List<string>();
    }

    /// <summary>Shows a Save As dialog.</summary>
    public static string? SaveFile(Window? owner, string title, string suggestedFileName, string filter)
    {
        var dlg = new SaveFileDialog { Title = title, FileName = suggestedFileName, Filter = filter };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    /// <summary>Shows a folder picker.</summary>
    public static string? PickFolder(Window? owner, string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }
}
