using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Acquires the PDF file for an <see cref="IndividualTemplate"/> by copying a local file
/// or downloading from the template's <c>OnlineLink</c>. Local copy is always attempted
/// first; if the local file is missing or the copy fails, the online link is used as a
/// fallback. Throws only when neither source is usable.
/// <para>
/// When <paramref name="allDescriptions"/> is supplied, the file is placed under a
/// <c>{Manufacturer}/{DescriptionHierarchy}/</c> subfolder inside <paramref name="jobSubfolder"/>,
/// mirroring the master template storage layout. When omitted the file is placed flat in
/// <paramref name="jobSubfolder"/> for backward-compatible callers.
/// </para>
/// </summary>
public class FileAcquirer
{
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new <see cref="FileAcquirer"/> with the given HTTP client.</summary>
    public FileAcquirer(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Acquires the PDF for the given template into <paramref name="jobSubfolder"/>.
    /// Local copy is attempted first; if unavailable or the copy fails, the online link
    /// is downloaded. Throws if neither source is usable.
    /// </summary>
    /// <param name="template">
    /// The template to acquire. <c>Manufacturer.ManufacturerName</c> must be loaded.
    /// </param>
    /// <param name="jobSubfolder">Base destination directory for the acquired file.</param>
    /// <param name="allDescriptions">
    /// When provided, the file is stored at
    /// <c>{Manufacturer}/{DescriptionHierarchy}/{TemplateNumber}.pdf</c> inside
    /// <paramref name="jobSubfolder"/>. Pass <c>null</c> to use a flat layout.
    /// </param>
    /// <returns>The full path to the acquired file.</returns>
    public async Task<string> AcquireAsync(
        IndividualTemplate template,
        string jobSubfolder,
        IReadOnlyList<Description>? allDescriptions = null)
    {
        var destPath = BuildDestPath(template, jobSubfolder, allDescriptions);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // --- Try local copy first ---
        if (!string.IsNullOrWhiteSpace(template.LocalLink) && File.Exists(template.LocalLink))
        {
            try
            {
                File.Copy(template.LocalLink, destPath, overwrite: true);
                return destPath;
            }
            catch
            {
                // Local copy failed (permissions, locked file, etc.) — fall through to online.
            }
        }

        // --- Fall back to online download ---
        if (!string.IsNullOrWhiteSpace(template.OnlineLink))
        {
            if (!System.Uri.TryCreate(template.OnlineLink, System.UriKind.Absolute, out _))
                throw new System.InvalidOperationException(
                    $"Template '{template.TemplateNumber}' has an invalid online link: '{template.OnlineLink}'. " +
                    "It must be a full URL (e.g. https://example.com/file.pdf).");

            await DownloadAsync(template.OnlineLink, destPath);
            return destPath;
        }

        throw new System.InvalidOperationException(
            $"Template '{template.TemplateNumber}' has no valid local file or online link.");
    }

    // ---------- Private helpers ----------

    private static string BuildDestPath(
        IndividualTemplate template,
        string jobSubfolder,
        IReadOnlyList<Description>? allDescriptions)
    {
        var fileName = $"{Sanitize(template.TemplateNumber)}.pdf";

        if (allDescriptions == null)
        {
            // Flat layout: {jobSubfolder}/{ManufacturerName}_{TemplateNumber}.pdf
            var flatName = $"{Sanitize(template.Manufacturer?.ManufacturerName ?? "Unknown")}_{fileName}";
            return Path.Combine(jobSubfolder, flatName);
        }

        // Organised layout: {jobSubfolder}/{Manufacturer}/{DescriptionHierarchy}/{TemplateNumber}.pdf
        var mfr      = Sanitize(template.Manufacturer?.ManufacturerName ?? "Unknown");
        var descPath = DescriptionPathService.GetFolderPath(template.DescriptionId, allDescriptions);

        return string.IsNullOrEmpty(descPath)
            ? Path.Combine(jobSubfolder, mfr, fileName)
            : Path.Combine(jobSubfolder, mfr, descPath, fileName);
    }

    /// <summary>
    /// Replaces any character that is invalid in a file or folder name on Windows or Linux
    /// with an underscore.
    /// </summary>
    private static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (invalid.Contains(chars[i]))
                chars[i] = '_';
        }
        return new string(chars);
    }

    /// <summary>
    /// Downloads the file at <paramref name="url"/> to <paramref name="destPath"/>.
    /// Validates that the response is a PDF by checking the Content-Type header and the
    /// <c>%PDF</c> magic bytes; throws <see cref="System.InvalidOperationException"/> if
    /// the server returns HTML or any other non-PDF content.
    /// </summary>
    private async Task DownloadAsync(string url, string destPath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        // Reject obvious HTML responses (redirect pages, auth walls, error pages).
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.StartsWith("text/html", System.StringComparison.OrdinalIgnoreCase))
            throw new System.InvalidOperationException(
                $"Download from '{url}' returned HTML instead of a PDF (Content-Type: {contentType}). " +
                "The server may require authentication or returned an error page. " +
                "Try refreshing the template's local file via the Refresh button.");

        // Read the full body so we can validate the magic bytes.
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // PDF files always begin with the 4-byte sequence %PDF (hex 25 50 44 46).
        if (bytes.Length < 4 ||
            bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46)
        {
            throw new System.InvalidOperationException(
                $"Download from '{url}' did not return a valid PDF file " +
                $"(Content-Type: '{contentType}', first bytes: {(bytes.Length >= 4 ? $"0x{bytes[0]:X2} 0x{bytes[1]:X2} 0x{bytes[2]:X2} 0x{bytes[3]:X2}" : "< 4 bytes received")}). " +
                "The server may have returned an error page or redirect. " +
                "Try refreshing the template's local file via the Refresh button.");
        }

        await using var fileStream = File.Create(destPath);
        await fileStream.WriteAsync(bytes);
    }
}
