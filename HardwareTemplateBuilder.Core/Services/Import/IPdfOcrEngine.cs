using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// Reads the words on scanned (image-only) PDF pages. Implemented by the desktop apps with the
/// text recognition built into Windows 10/11; <see cref="HardwarePdfTextReader"/> uses it for
/// pages that have no text of their own.
/// </summary>
public interface IPdfOcrEngine
{
    /// <summary>True when text recognition can run on this computer.</summary>
    bool IsAvailable { get; }

    /// <summary>Recognises the words on one page.</summary>
    /// <param name="pdfPath">The PDF file.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The page's words, in any consistent unit with a top-left origin.</returns>
    Task<IReadOnlyList<PdfWord>> RecognizePageAsync(string pdfPath, int pageIndex, CancellationToken cancellationToken);
}
