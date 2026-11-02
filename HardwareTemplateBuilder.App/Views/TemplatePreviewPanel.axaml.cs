using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using HardwareTemplateBuilder.Core.Models;
using PDFtoImage;
using SkiaSharp;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Reusable PDF preview control that renders the pages of an <see cref="IndividualTemplate"/>'s
/// source PDF (sourced from its <see cref="IndividualTemplate.LocalLink"/> file if present on
/// disk, otherwise downloaded from its <see cref="IndividualTemplate.OnlineLink"/>), with Prev/
/// Next page navigation. Used anywhere a user selects an existing template to link or import, so
/// they can confirm it's the right document before committing — mirrors the preview on
/// <see cref="TemplateLookupView"/>.
/// </summary>
public partial class TemplatePreviewPanel : UserControl
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private byte[]? _pdfBytes;
    private int _pageCount;
    private int _pageIndex;
    private CancellationTokenSource? _cts;

    /// <summary>Initializes the preview panel and wires its navigation buttons.</summary>
    public TemplatePreviewPanel()
    {
        InitializeComponent();
        PrevPageButton.Click += async (_, _) => await ChangePageAsync(-1);
        NextPageButton.Click += async (_, _) => await ChangePageAsync(1);
        PdfScrollViewer.SizeChanged += (_, _) => FitToViewportWidth();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler);
    }

    /// <summary>
    /// Loads and renders the first page of the given template's PDF, or clears the preview when
    /// <paramref name="template"/> is null.
    /// </summary>
    public async Task LoadTemplateAsync(IndividualTemplate? template)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        if (template is null)
        {
            Clear();
            return;
        }

        try
        {
            Clear();
            PageLabel.Text = "Loading preview...";
            var bytes = await LoadBytesAsync(template, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (bytes is null)
            {
                PageLabel.Text = "No preview available.";
                return;
            }

            _pdfBytes = bytes;
            _pageCount = Conversion.GetPageCount(bytes);
            _pageIndex = 0;
            await RenderPageAsync();
        }
        catch (Exception ex)
        {
            Clear();
            PageLabel.Text = $"Could not preview PDF: {ex.Message}";
        }
    }

    private static async Task<byte[]?> LoadBytesAsync(IndividualTemplate template, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(template.LocalLink) && File.Exists(template.LocalLink))
            return await File.ReadAllBytesAsync(template.LocalLink, ct);

        if (!string.IsNullOrWhiteSpace(template.OnlineLink))
            return await SharedHttpClient.GetByteArrayAsync(template.OnlineLink, ct);

        return null;
    }

    private async Task ChangePageAsync(int delta)
    {
        if (_pdfBytes is null) return;

        var newIndex = _pageIndex + delta;
        if (newIndex < 0 || newIndex >= _pageCount) return;

        _pageIndex = newIndex;
        await RenderPageAsync();
    }

    private async Task RenderPageAsync()
    {
        if (_pdfBytes is null) return;

        var pdfBytes = _pdfBytes;
        var pageIndex = _pageIndex;

        // Rasterizing a PDF page is CPU-heavy; run it on a background thread so the UI stays
        // responsive instead of freezing for the duration.
        var pngBytes = await Task.Run(() =>
        {
            using SKBitmap skBitmap = Conversion.ToImage(pdfBytes, new Index(pageIndex));
            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        });

        using var stream = new MemoryStream(pngBytes);
        PdfPreviewImage.Source = new Bitmap(stream);

        PageLabel.Text = $"Page {_pageIndex + 1} of {_pageCount}";
        PrevPageButton.IsEnabled = _pageIndex > 0;
        NextPageButton.IsEnabled = _pageIndex < _pageCount - 1;

        FitToViewportWidth();
    }

    private void FitToViewportWidth()
    {
        if (PdfPreviewImage.Source is null) return;

        var viewportWidth = PdfScrollViewer.Viewport.Width;
        if (viewportWidth > 0)
            PdfPreviewImage.Width = viewportWidth;
    }

    private void Clear()
    {
        _pdfBytes = null;
        _pageCount = 0;
        _pageIndex = 0;
        PdfPreviewImage.Source = null;
        PageLabel.Text = "No template selected";
        PrevPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
    }
}
