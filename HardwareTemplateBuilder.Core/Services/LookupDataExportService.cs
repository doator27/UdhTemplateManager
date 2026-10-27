using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models.Lookup;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Exports a <see cref="LookupDataSnapshot"/> of hardware item/template metadata to
/// <c>data.json</c> (see <see cref="LookupDataLocationService"/>) so the standalone Lookup app
/// can search and merge templates without a direct database connection. Called after every
/// "Refresh Templates" run in the main app.
/// </summary>
public class LookupDataExportService
{
    private readonly AppDbContext _context;

    /// <summary>Initializes a new <see cref="LookupDataExportService"/>.</summary>
    public LookupDataExportService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Builds the current snapshot from the database and writes it to the resolved
    /// <c>data.json</c> path, overwriting any existing file.
    /// </summary>
    public string Export()
    {
        var saveLocation = new AppSettingRepository(_context).GetValue("TemplateStorageLocation")
            ?? string.Empty;

        var snapshot = new LookupDataSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TemplateStorageLocation = saveLocation,
            Manufacturers = _context.Manufacturers
                .Select(m => new LookupManufacturer { Id = m.Id, ManufacturerName = m.ManufacturerName })
                .ToList(),
            Descriptions = _context.Descriptions
                .Select(d => new LookupDescription
                {
                    Id = d.Id,
                    DescriptionText = d.DescriptionText,
                    ParentId = d.ParentId,
                    SortOrder = d.SortOrder,
                    IsActive = d.IsActive
                })
                .ToList(),
            HardwareItems = _context.HardwareItems
                .Where(h => h.IsActive)
                .Select(h => new LookupHardwareItem
                {
                    Id = h.Id,
                    ManufacturerId = h.ManufacturerId,
                    DescriptionId = h.DescriptionId,
                    ModelNumber = h.ModelNumber,
                    IsActive = h.IsActive,
                    Frequency = h.Frequency
                })
                .ToList(),
            Templates = _context.IndividualTemplates
                .Select(t => new LookupIndividualTemplate
                {
                    Id = t.Id,
                    ManufacturerId = t.ManufacturerId,
                    DescriptionId = t.DescriptionId,
                    TemplateNumber = t.TemplateNumber,
                    NumPages = t.NumPages,
                    PagesToPrint = t.PagesToPrint,
                    PagesToRotate = t.PagesToRotate,
                    RotationDirection = t.RotationDirection,
                    OnlineLink = t.OnlineLink,
                    LocalLink = t.LocalLink
                })
                .ToList(),
            HardwareItemTemplates = _context.HardwareItemTemplates
                .Where(hit => hit.JobId == null)
                .Select(hit => new LookupHardwareItemTemplate
                {
                    HardwareItemId = hit.HardwareItemId,
                    IndividualTemplateId = hit.IndividualTemplateId
                })
                .ToList(),
            // Only include labels for hardware items that are themselves exported (IsActive),
            // otherwise the loader can't resolve the label's HardwareItemId and would silently
            // drop it.
            Labels = _context.JobHardware
                .Where(jh => !string.IsNullOrWhiteSpace(jh.CustomDescription)
                    && jh.HardwareItem.IsActive)
                .Select(jh => new { jh.CustomDescription, jh.HardwareItemId })
                .Distinct()
                .ToList()
                .Select(x => new LookupJobHardwareLabel
                {
                    Label = x.CustomDescription!.Trim(),
                    HardwareItemId = x.HardwareItemId
                })
                .GroupBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList()
        };

        var path = LookupDataLocationService.GetDataFilePath();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }
}
