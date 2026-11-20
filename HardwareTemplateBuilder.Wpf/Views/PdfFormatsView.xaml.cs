using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>Maintenance → PDF Import Formats: the formats taught to Parse PDF into Bulk Add (<see cref="CustomPdfFormats"/>).</summary>
public partial class PdfFormatsView : UserControl
{
    /// <summary>The route of this view.</summary>
    public const string Route = "PdfFormats";

    /// <summary>Creates the view.</summary>
    public PdfFormatsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        TeachButton.Click += async (_, _) => await TeachAsync(null);
        EditButton.Click += async (_, _) =>
        {
            if (FormatsList.SelectedItem is CustomPdfFormat f) await TeachAsync(f);
            else Status("Select a format first.", AppColors.Danger);
        };
        DeleteButton.Click += async (_, _) => await DeleteAsync();
        FormatsList.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            DetailsText.Text = FormatsList.SelectedItem is CustomPdfFormat f ? CustomPdfFormats.Describe(f) : "Select a format.";
        };
        Load(null);
    }

    private void Load(string? select)
    {
        List<CustomPdfFormat> formats;
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            formats = CustomPdfFormats.Load(ctx);
        }
        catch (Exception ex)
        {
            Status($"Couldn't load the formats: {ex.Message}", AppColors.Danger);
            return;
        }
        FormatsList.ItemsSource = formats;
        FormatsList.SelectedItem = formats.FirstOrDefault(f => f.Name == select) ?? formats.FirstOrDefault();
        if (formats.Count == 0) DetailsText.Text = "No formats taught yet. Click Teach New Format... and pick a PDF of the kind the app can't read.";
    }

    /// <summary>Picks a PDF and opens the teaching window (for a new format, or to change <paramref name="editing"/>).</summary>
    private async Task TeachAsync(CustomPdfFormat? editing)
    {
        var window = this.OwnerWindow();
        var path = FilePickers.OpenFile(window, editing == null ? "Choose a PDF of the new format" : $"Choose a PDF to check \"{editing.Name}\" against", "PDF Files (*.pdf)|*.pdf");
        if (path == null) return;
        if (await new TeachPdfFormatWindow(path, editing).ShowDialogAsync<bool?>(window) == true)
        {
            Status("Format saved. Parse PDF into Bulk Add will use it for PDFs like this one.", AppColors.Success);
            Load(null);
        }
    }

    private async Task DeleteAsync()
    {
        if (FormatsList.SelectedItem is not CustomPdfFormat f) return;
        var window = this.OwnerWindow();
        if (!await DialogHelper.ConfirmAsync(window, $"Delete the format \"{f.Name}\"? PDFs of that kind will be read by the built-in formats again.", "Delete Format")) return;
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            CustomPdfFormats.Remove(ctx, f.Name);
        }
        catch (Exception ex)
        {
            Status($"Couldn't delete: {ex.Message}", AppColors.Danger);
            return;
        }
        Status($"Deleted \"{f.Name}\".", AppColors.Success);
        Load(null);
    }

    private void Status(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }
}
