namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// One word on a PDF page with its box in page units, origin top-left (y grows downward).
/// Produced by the PDF text reader for text PDFs and by an <see cref="IPdfOcrEngine"/> for scans.
/// </summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
/// <param name="Text">The word.</param>
public sealed record PdfWord(double Left, double Top, double Right, double Bottom, string Text);
