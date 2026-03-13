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
    /// Refreshes all templates with a non-null <c>OnlineLink</c>, downloading each file to
    /// <paramref name="saveLocation"/> and updating <c>LocalLink</c> on success.
    /// Failures are collected and returned — they do not stop remaining downloads.
    /// </summary>
    /// <param name="saveLocation">
    /// Directory where downloaded files are saved.
    /// Files are named <c>{ManufacturerName}_{TemplateNumber}.pdf</c>.
    /// </param>
    /// <param name="progress">Optional progress reporter called before each download.</param>
    /// <param name="cancellationToken">Token to cancel the entire refresh.</param>
    /// <returns>A <see cref="RefreshResult"/> summarising successes and failures.</returns>
    public async Task<RefreshResult> RefreshAsync(
        string saveLocation,
        IProgress<RefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var templates = _context.IndividualTemplates
            .Include(t => t.Manufacturer)
            .Where(t => t.OnlineLink != null && t.OnlineLink != string.Empty)
            .ToList();

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
                TemplateName = fileName
            });

            try
            {
                var destPath = Path.Combine(saveLocation, fileName);
                await DownloadAsync(template.OnlineLink!, destPath, cancellationToken);

                // Persist the updated local path immediately so partial progress is not lost.
                template.LocalLink = destPath;
                _context.SaveChanges();
                successCount++;
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation so the caller knows the loop was cut short.
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new RefreshFailure
                {
                    TemplateId   = template.Id,
                    TemplateName = $"{template.Manufacturer?.ManufacturerName} {template.TemplateNumber}",
                    ErrorMessage = ex.Message
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

        Directory.CreateDirectory(saveLocation);
        var destPath = Path.Combine(saveLocation, BuildFileName(template));
        await DownloadAsync(template.OnlineLink!, destPath, cancellationToken);
        template.LocalLink = destPath;
        _context.SaveChanges();
        return true;
    }

    // ---------- Private helpers ----------

    /// <summary>
    /// Builds the output file name for a template:
    /// <c>{ManufacturerName}_{TemplateNumber}.pdf</c> with invalid path chars replaced.
    /// </summary>
    private static string BuildFileName(IndividualTemplate template)
    {
        var mfr    = Sanitize(template.Manufacturer?.ManufacturerName ?? "Unknown");
        var number = Sanitize(template.TemplateNumber);
        return $"{mfr}_{number}.pdf";
    }

    /// <summary>Replaces file-name-invalid characters with underscores.</summary>
    private static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Downloads the file at <paramref name="url"/> to <paramref name="destPath"/>.</summary>
    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(destPath);
        await response.Content.CopyToAsync(fs, ct);
    }
}
