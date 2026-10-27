using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Models.Lookup;

namespace HardwareTemplateBuilder.Lookup;

/// <summary>
/// Loads the <c>data.json</c> snapshot exported by the main app and reconstructs in-memory
/// <see cref="Manufacturer"/>/<see cref="Description"/>/<see cref="HardwareItem"/>/
/// <see cref="IndividualTemplate"/> object graphs (with navigation properties wired) so the
/// existing sort/merge code can run unmodified against data that didn't come from a live
/// database connection.
/// </summary>
public class LookupDataLoader
{
    /// <summary>Gets the configured template storage location at export time.</summary>
    public string TemplateStorageLocation { get; private set; } = string.Empty;

    /// <summary>Gets every manufacturer, ordered by name.</summary>
    public List<Manufacturer> Manufacturers { get; private set; } = new();

    /// <summary>Gets every description node.</summary>
    public List<Description> Descriptions { get; private set; } = new();

    /// <summary>Gets every active hardware item, with Manufacturer/Description/HardwareItemTemplates wired.</summary>
    public List<HardwareItem> HardwareItems { get; private set; } = new();

    /// <summary>
    /// Gets every distinct job-assigned custom label, mapped (case-insensitively) to the
    /// hardware item it was used for.
    /// </summary>
    public Dictionary<string, HardwareItem> LabelsToHardwareItem { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the UTC timestamp the loaded snapshot was generated at, or null if none was loaded.</summary>
    public DateTime? GeneratedAtUtc { get; private set; }

    /// <summary>
    /// Attempts to load and parse <c>data.json</c>. Returns <c>false</c> (leaving all collections
    /// empty) if the file doesn't exist or fails to parse.
    /// </summary>
    public bool TryLoad(out string? error)
    {
        var path = LookupDataLocationService.GetDataFilePath();
        if (!File.Exists(path))
        {
            error = $"No data file found at \"{path}\". Run \"Refresh Templates\" in Hardware Template Builder first.";
            return false;
        }

        LookupDataSnapshot snapshot;
        try
        {
            var json = File.ReadAllText(path);
            snapshot = JsonSerializer.Deserialize<LookupDataSnapshot>(json)
                ?? throw new InvalidDataException("Snapshot deserialized to null.");
        }
        catch (Exception ex)
        {
            error = $"Could not read \"{path}\": {ex.Message}";
            return false;
        }

        Build(snapshot);
        error = null;
        return true;
    }

    private void Build(LookupDataSnapshot snapshot)
    {
        GeneratedAtUtc = snapshot.GeneratedAtUtc;
        TemplateStorageLocation = snapshot.TemplateStorageLocation;

        var manufacturers = snapshot.Manufacturers
            .Select(m => new Manufacturer { Id = m.Id, ManufacturerName = m.ManufacturerName })
            .ToDictionary(m => m.Id);

        var descriptions = snapshot.Descriptions
            .Select(d => new Description
            {
                Id = d.Id,
                DescriptionText = d.DescriptionText,
                ParentId = d.ParentId,
                SortOrder = d.SortOrder,
                IsActive = d.IsActive
            })
            .ToDictionary(d => d.Id);

        foreach (var d in descriptions.Values)
        {
            if (d.ParentId is int parentId && descriptions.TryGetValue(parentId, out var parent))
            {
                d.Parent = parent;
                parent.Children.Add(d);
            }
        }

        var templates = snapshot.Templates
            .Select(t => new IndividualTemplate
            {
                Id = t.Id,
                ManufacturerId = t.ManufacturerId,
                Manufacturer = manufacturers.TryGetValue(t.ManufacturerId, out var mfr) ? mfr : null!,
                DescriptionId = t.DescriptionId,
                Description = descriptions.TryGetValue(t.DescriptionId, out var desc) ? desc : null!,
                TemplateNumber = t.TemplateNumber,
                NumPages = t.NumPages,
                PagesToPrint = t.PagesToPrint,
                PagesToRotate = t.PagesToRotate,
                RotationDirection = t.RotationDirection,
                OnlineLink = t.OnlineLink,
                LocalLink = t.LocalLink
            })
            .ToDictionary(t => t.Id);

        var hardwareItems = snapshot.HardwareItems
            .Select(h => new HardwareItem
            {
                Id = h.Id,
                ManufacturerId = h.ManufacturerId,
                Manufacturer = manufacturers.TryGetValue(h.ManufacturerId, out var mfr) ? mfr : null!,
                DescriptionId = h.DescriptionId,
                Description = descriptions.TryGetValue(h.DescriptionId, out var desc) ? desc : null!,
                ModelNumber = h.ModelNumber,
                IsActive = h.IsActive,
                Frequency = h.Frequency
            })
            .ToDictionary(h => h.Id);

        foreach (var link in snapshot.HardwareItemTemplates)
        {
            if (!hardwareItems.TryGetValue(link.HardwareItemId, out var item)) continue;
            if (!templates.TryGetValue(link.IndividualTemplateId, out var template)) continue;

            var junction = new HardwareItemTemplate
            {
                HardwareItemId = item.Id,
                HardwareItem = item,
                IndividualTemplateId = template.Id,
                IndividualTemplate = template
            };
            item.HardwareItemTemplates.Add(junction);
            template.HardwareItemTemplates.Add(junction);
        }

        Manufacturers = manufacturers.Values.OrderBy(m => m.ManufacturerName).ToList();
        Descriptions = descriptions.Values.ToList();
        HardwareItems = hardwareItems.Values.ToList();

        var labelsToHardwareItem = new Dictionary<string, HardwareItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in snapshot.Labels)
        {
            if (string.IsNullOrWhiteSpace(label.Label)) continue;
            if (!hardwareItems.TryGetValue(label.HardwareItemId, out var item)) continue;
            labelsToHardwareItem[label.Label] = item;
        }
        LabelsToHardwareItem = labelsToHardwareItem;
    }
}
