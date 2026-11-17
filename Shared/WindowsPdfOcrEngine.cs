#if WINDOWS10_0_19041_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Services.Import;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HardwareTemplateBuilder.Shared;

/// <summary>
/// Reads scanned PDF pages with the text recognition built into Windows 10/11: Windows renders
/// the page (Windows.Data.Pdf) and Windows.Media.Ocr returns the words with their positions.
/// Compiled into both desktop apps (linked source) on Windows builds only.
/// </summary>
public sealed class WindowsPdfOcrEngine : IPdfOcrEngine
{
    private OcrEngine? _engine;
    private bool _created;

    /// <inheritdoc />
    public bool IsAvailable => GetEngine() != null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PdfWord>> RecognizePageAsync(string pdfPath, int pageIndex, CancellationToken cancellationToken)
    {
        var engine = GetEngine() ?? throw new InvalidOperationException("Windows text recognition isn't available on this computer.");

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(cancellationToken);
        var document = await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
        using var page = document.GetPage((uint)pageIndex);

        // Render at up to 3x (about 288 dpi), within the OCR engine's size limit.
        double longest = Math.Max(page.Size.Width, page.Size.Height);
        double scale = Math.Min(3.0, OcrEngine.MaxImageDimension / Math.Max(1.0, longest));
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)(page.Size.Width * scale),
            DestinationHeight = (uint)(page.Size.Height * scale),
        }).AsTask(cancellationToken);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

        var words = new List<PdfWord>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new PdfWord(r.X, r.Y, r.X + r.Width, r.Y + r.Height, word.Text));
            }
        }
        return words;
    }

    private OcrEngine? GetEngine()
    {
        if (_created) return _engine;
        _created = true;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        }
        catch
        {
            _engine = null;
        }
        return _engine;
    }
}
#endif
