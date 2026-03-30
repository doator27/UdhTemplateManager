using System.Collections.Generic;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App;

// ── JSON DTO classes (version 2 draft format) ─────────────────────────────────

/// <summary>Root object serialised into <c>BulkAddDraft.DraftJson</c>.</summary>
internal sealed class BulkDraftV2
{
    public int Version { get; set; } = 2;

    /// <summary>Ordered list of manufacturer IDs chosen for this job.</summary>
    public List<int> ManufacturerIds { get; set; } = new();

    /// <summary>All hardware items entered across all manufacturers.</summary>
    public List<BulkDraftItem> Items { get; set; } = new();
}

/// <summary>One hardware item row in the draft.</summary>
internal sealed class BulkDraftItem
{
    public int  ManufacturerId { get; set; }
    public int? DescriptionId  { get; set; }
    public string ModelNumber  { get; set; } = "";
    public List<BulkDraftLabel> Labels { get; set; } = new();
}

/// <summary>One custom-label entry within a draft item.</summary>
internal sealed class BulkDraftLabel
{
    public string  CustomLabel { get; set; } = "";
    public string? Remarks     { get; set; }
}

// ── Static helper ─────────────────────────────────────────────────────────────

/// <summary>
/// Provides read/write helpers for the v2 bulk-add draft stored in the
/// <c>BulkAddDrafts</c> table.
/// </summary>
internal static class BulkDraftService
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy    = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads the draft for <paramref name="jobId"/>, returning an empty
    /// <see cref="BulkDraftV2"/> if none exists or the JSON is unreadable.
    /// </summary>
    public static BulkDraftV2 Load(int jobId)
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var draft = new BulkAddDraftRepository(ctx).GetByJob(jobId);
            if (draft == null || string.IsNullOrWhiteSpace(draft.DraftJson)) return new();

            // Attempt v2 parse; fall back to empty on any error.
            var doc = JsonDocument.Parse(draft.DraftJson);
            if (!doc.RootElement.TryGetProperty("version", out var ver) || ver.GetInt32() < 2)
                return new();   // Old v1 draft — discard; v1 format is incompatible.

            return JsonSerializer.Deserialize<BulkDraftV2>(draft.DraftJson, _opts) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Persists <paramref name="draft"/> for <paramref name="jobId"/>.</summary>
    public static void Save(int jobId, BulkDraftV2 draft)
    {
        try
        {
            var json = JsonSerializer.Serialize(draft, _opts);
            using var ctx = DatabaseInitializer.CreateContext();
            new BulkAddDraftRepository(ctx).Upsert(jobId, json);
        }
        catch { /* Non-fatal */ }
    }

    /// <summary>Deletes the draft for <paramref name="jobId"/>.</summary>
    public static void Delete(int jobId)
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            new BulkAddDraftRepository(ctx).DeleteByJob(jobId);
        }
        catch { /* Non-fatal */ }
    }
}
