using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Progress information reported for each template during a refresh operation.
/// </summary>
public class RefreshProgress
{
    /// <summary>Gets the 1-based index of the template currently being processed.</summary>
    public int Current { get; init; }

    /// <summary>Gets the total number of templates to process.</summary>
    public int Total { get; init; }

    /// <summary>Gets a display name for the template being processed.</summary>
    public string TemplateName { get; init; } = string.Empty;

    /// <summary>Gets the ID (IndividualTemplates table row) of the template being processed.</summary>
    public int TemplateId { get; init; }
}

/// <summary>
/// Describes a single download failure during a refresh operation.
/// </summary>
public class RefreshFailure
{
    /// <summary>Gets the ID of the template that failed to download.</summary>
    public int TemplateId { get; init; }

    /// <summary>Gets a human-readable name for the template (manufacturer + template number).</summary>
    public string TemplateName { get; init; } = string.Empty;

    /// <summary>Gets the error message returned by the failed download attempt.</summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>Gets the online link that was attempted, for inclusion in failure reports.</summary>
    public string OnlineLink { get; init; } = string.Empty;
}

/// <summary>
/// Summary of a completed refresh operation.
/// </summary>
public class RefreshResult
{
    /// <summary>Gets the number of templates that were successfully downloaded and updated.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Gets the total number of templates that were attempted.</summary>
    public int TotalAttempted { get; init; }

    /// <summary>Gets the list of failures, one per template that could not be downloaded.</summary>
    public IReadOnlyList<RefreshFailure> Failures { get; init; } = Array.Empty<RefreshFailure>();
}

/// <summary>
/// Bulk-downloads all <see cref="IndividualTemplate"/> records that have an online link,
/// updates their <c>LocalLink</c> in the database on success, and reports failures without
/// stopping the remaining downloads.
/// <para>
/// <c>JobTemplateSnapshot</c> records are never touched by this service.
/// </para>
/// </summary>
public class TemplateRefreshService
{
    private readonly AppDbContext _context;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new <see cref="TemplateRefreshService"/>.
    /// </summary>
    /// <param name="context">Database context for reading templates and persisting LocalLink updates.</param>
    /// <param name="httpClient">HTTP client used for downloading template files.</param>
    public TemplateRefreshService(AppDbContext context, HttpClient httpClient)
    {
        _context   = context;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Refreshes all templates with a non-null <c>OnlineLink</c>, downloading each file into
    /// an organised folder hierarchy under <paramref name="saveLocation"/> and updating
    /// <c>LocalLink</c> on success. Failures are collected and returned without stopping
    /// remaining downloads.
    /// </summary>
    /// <param name="saveLocation">
    /// Root directory for downloaded files. Each file is stored at
    /// <c>{saveLocation}/{Manufacturer}/{DescriptionHierarchy}/{TemplateNumber}.pdf</c>.
    /// </param>
    /// <param name="progress">Optional progress reporter called before each download.</param>
    /// <param name="cancellationToken">Token to cancel the entire refresh.</param>
    /// <returns>A <see cref="RefreshResult"/> summarising successes and failures.</returns>
    public async Task<RefreshResult> RefreshAsync(
        string saveLocation,
        IProgress<RefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var templates = await _context.IndividualTemplates
            .Include(t => t.Manufacturer)
            .Where(t => t.OnlineLink != null && t.OnlineLink != string.Empty)
            .ToListAsync(cancellationToken);

        // Load all descriptions once to avoid repeated queries inside the loop.
        var allDescriptions = await _context.Descriptions.ToListAsync(cancellationToken);

        Directory.CreateDirectory(saveLocation);

        int successCount = 0;
        var failures = new List<RefreshFailure>();
        int total = templates.Count;

        for (int i = 0; i < templates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var template = templates[i];
            var fileName = BuildFileName(template);

            progress?.Report(new RefreshProgress
            {
                Current      = i + 1,
                Total        = total,
                TemplateName = fileName,
                TemplateId   = template.Id
            });

            try
            {
                var destPath = BuildDestPath(saveLocation, template, allDescriptions, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await DownloadAsync(template.OnlineLink!, destPath, cancellationToken);

                // Persist the updated local path immediately so partial progress is not lost.
                template.LocalLink = destPath;
                await _context.SaveChangesAsync(cancellationToken);
                successCount++;
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation so the caller knows the loop was cut short.
                throw;
            }
            catch (Exception ex)
            {
                // If SaveChangesAsync failed after LocalLink was set in-memory, reset the
                // entity to Unchanged so the dirty change does not contaminate subsequent
                // SaveChanges calls for other templates in the same loop iteration.
                _context.Entry(template).State = EntityState.Unchanged;
                failures.Add(new RefreshFailure
                {
                    TemplateId   = template.Id,
                    TemplateName = $"{template.Manufacturer?.ManufacturerName} {template.TemplateNumber}",
                    ErrorMessage = ex.Message,
                    OnlineLink   = template.OnlineLink ?? string.Empty
                });
            }
        }

        return new RefreshResult
        {
            SuccessCount   = successCount,
            TotalAttempted = total,
            Failures       = failures.AsReadOnly()
        };
    }

    /// <summary>
    /// Downloads and updates a single template by its ID.
    /// Does nothing and returns false if the template has no <c>OnlineLink</c>.
    /// </summary>
    /// <param name="templateId">Primary key of the template to refresh.</param>
    /// <param name="saveLocation">Directory where the downloaded file is saved.</param>
    /// <param name="cancellationToken">Token to cancel the download.</param>
    /// <returns>True if the file was downloaded and <c>LocalLink</c> was updated; false otherwise.</returns>
    public async Task<bool> RefreshSingleAsync(
        int templateId,
        string saveLocation,
        CancellationToken cancellationToken = default)
    {
        var template = _context.IndividualTemplates
            .Include(t => t.Manufacturer)
            .FirstOrDefault(t => t.Id == templateId);

        if (template == null || string.IsNullOrWhiteSpace(template.OnlineLink))
            return false;

        var allDescriptions = _context.Descriptions.ToList();
        var fileName = BuildFileName(template);
        var destPath = BuildDestPath(saveLocation, template, allDescriptions, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await DownloadAsync(template.OnlineLink!, destPath, cancellationToken);
        template.LocalLink = destPath;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Reset entity state so a failed LocalLink update does not persist on any
            // subsequent SaveChanges call made against the same context.
            _context.Entry(template).State = EntityState.Unchanged;
            throw;
        }
        return true;
    }

    // ---------- Private helpers ----------

    /// <summary>
    /// Builds the full destination path for a template file:
    /// <c>{saveLocation}/{Manufacturer}/{DescriptionHierarchy}/{TemplateNumber}.pdf</c>.
    /// </summary>
    private static string BuildDestPath(
        string saveLocation,
        IndividualTemplate template,
        IReadOnlyList<Description> allDescriptions,
        string fileName)
    {
        var mfrSegment  = Sanitize(template.Manufacturer?.ManufacturerName ?? "Unknown");
        var descSegment = DescriptionPathService.GetFolderPath(template.DescriptionId, allDescriptions);

        return string.IsNullOrEmpty(descSegment)
            ? Path.Combine(saveLocation, "Templates", mfrSegment, fileName)
            : Path.Combine(saveLocation, "Templates", mfrSegment, descSegment, fileName);
    }

    /// <summary>
    /// Builds the output file name for a template: <c>{TemplateNumber}.pdf</c>
    /// with invalid file-name characters replaced by underscores.
    /// </summary>
    private static string BuildFileName(IndividualTemplate template)
    {
        return $"{Sanitize(template.TemplateNumber)}.pdf";
    }

    /// <summary>Replaces file-name-invalid characters with underscores.</summary>
    private static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Maximum number of retry attempts for a transient download failure (network-level, not content-mismatch).</summary>
    private const int MaxDownloadRetries = 2;

    /// <summary>Base delay between retries; doubles each attempt (500ms, 1000ms, ...).</summary>
    private const int RetryBaseDelayMs = 500;

    /// <summary>
    /// Downloads the file at <paramref name="url"/> to <paramref name="destPath"/>, retrying a
    /// handful of times on transient network failures (connection resets, timeouts). Does not
    /// retry when the server responds but with the wrong content — that's a deterministic
    /// mismatch a retry cannot fix.
    /// </summary>
    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destPath, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Caller-requested cancellation — never retry.
            }
            catch (Exception ex) when (attempt < MaxDownloadRetries && IsTransientDownloadError(ex))
            {
                await Task.Delay(RetryBaseDelayMs * (int)Math.Pow(2, attempt), ct);
            }
        }
    }

    /// <summary>
    /// True for network-level failures worth retrying (the request may simply not have reached
    /// the server). False for <see cref="InvalidOperationException"/> content-mismatch failures,
    /// where the server responded successfully but with the wrong content — retrying would just
    /// get the same wrong response again.
    /// </summary>
    private static bool IsTransientDownloadError(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException;

    /// <summary>
    /// Makes a single download attempt. Validates that the response is a PDF by checking the
    /// Content-Type header and the <c>%PDF</c> magic bytes; throws
    /// <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    private async Task DownloadOnceAsync(string url, string destPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        // Deliberately no Origin/Referer headers: previously added unconditionally for every
        // host on the theory that some APIs (e.g. abhmfg.com) need them to resolve tenant
        // context, but confirmed that host now works fine without them, while at least one
        // real source (an S3 bucket with referrer-based hotlink protection) actively returns
        // 403 Forbidden when a non-matching Referer is present. Omitting them is safe for every
        // host currently in use and avoids that failure mode.
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        if (bytes.Length < 4 ||
            bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46)
        {
            var bodyPreview = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 300))
                                     .Replace("\r", "").Replace("\n", " ");

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The server returned a webpage instead of a PDF (HTTP {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}, Content-Type: '{contentType}'). The manufacturer's " +
                    $"website has likely reorganized its files and this link is now outdated — " +
                    $"the correct download URL will need to be found and updated manually. " +
                    $"Page preview: \"{bodyPreview}\"");
            }

            throw new InvalidOperationException(
                $"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}, " +
                $"Content-Type: '{contentType}', " +
                $"body: \"{bodyPreview}\".");
        }

        await using var fs = File.Create(destPath);
        await fs.WriteAsync(bytes, ct);
    }
}
