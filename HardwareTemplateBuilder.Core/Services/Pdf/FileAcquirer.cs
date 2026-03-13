using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Acquires the PDF file for an <see cref="IndividualTemplate"/> by either copying a local
/// file or downloading from the template's <c>OnlineLink</c>. The output is placed in the
/// specified job subfolder and named <c>{ManufacturerName}_{TemplateNumber}.pdf</c>.
/// </summary>
public class FileAcquirer
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new <see cref="FileAcquirer"/> with the given HTTP client.
    /// </summary>
    /// <param name="httpClient">Client used for downloading templates from online links.</param>
    public FileAcquirer(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Acquires the PDF for the given template into <paramref name="jobSubfolder"/>.
    /// If the template has a valid local file it is copied; otherwise the online link is
    /// downloaded. Throws if neither source is available.
    /// </summary>
    /// <param name="template">
    /// The template to acquire. <c>Manufacturer.ManufacturerName</c> must be loaded.
    /// </param>
    /// <param name="jobSubfolder">Destination directory for the acquired file.</param>
    /// <returns>The full path to the acquired file in <paramref name="jobSubfolder"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the template has neither a valid local file nor an online link.
    /// </exception>
    public async Task<string> AcquireAsync(IndividualTemplate template, string jobSubfolder)
    {
        Directory.CreateDirectory(jobSubfolder);

        var fileName = BuildFileName(template);
        var destinationPath = Path.Combine(jobSubfolder, fileName);

        if (!string.IsNullOrWhiteSpace(template.LocalLink) && File.Exists(template.LocalLink))
        {
            File.Copy(template.LocalLink, destinationPath, overwrite: true);
            return destinationPath;
        }

        if (!string.IsNullOrWhiteSpace(template.OnlineLink))
        {
            await DownloadAsync(template.OnlineLink, destinationPath);
            return destinationPath;
        }

        throw new InvalidOperationException(
            $"Template '{template.TemplateNumber}' has no valid local file or online link.");
    }

    /// <summary>
    /// Builds the output file name: <c>{ManufacturerName}_{TemplateNumber}.pdf</c>.
    /// Characters invalid in file names on any supported platform are replaced with underscores.
    /// </summary>
    private static string BuildFileName(IndividualTemplate template)
    {
        var manufacturerName = template.Manufacturer?.ManufacturerName ?? "Unknown";
        var templateNumber = template.TemplateNumber;

        manufacturerName = Sanitize(manufacturerName);
        templateNumber = Sanitize(templateNumber);

        return $"{manufacturerName}_{templateNumber}.pdf";
    }

    /// <summary>
    /// Replaces any character that is invalid in a file name on Windows or Linux with an
    /// underscore. Includes the OS-reported invalid chars plus <c>:</c>, <c>/</c>, and
    /// <c>\</c> which may not be flagged on all platforms.
    /// </summary>
    private static string Sanitize(string name)
    {
        // Union of Path.GetInvalidFileNameChars() and characters that are
        // invalid on Windows but not always reported on Linux (colon, slashes).
        var extraInvalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };

        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (extraInvalid.Contains(chars[i]))
                chars[i] = '_';
        }
        return new string(chars);
    }

    /// <summary>Downloads the file at <paramref name="url"/> to <paramref name="destinationPath"/>.</summary>
    private async Task DownloadAsync(string url, string destinationPath)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(destinationPath);
        await response.Content.CopyToAsync(fileStream);
    }
}
