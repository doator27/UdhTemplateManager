using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="IndividualTemplate"/> entities.</summary>
public class IndividualTemplateRepository : RepositoryBase<IndividualTemplate>
{
    /// <inheritdoc/>
    public IndividualTemplateRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override IndividualTemplate? FindDuplicate(IndividualTemplate entity) =>
        _context.IndividualTemplates.FirstOrDefault(t =>
            t.ManufacturerId == entity.ManufacturerId &&
            t.TemplateNumber == entity.TemplateNumber &&
            t.DoorMaterialId == entity.DoorMaterialId);

    /// <summary>
    /// Returns all templates visible for the given job: globally-visible templates
    /// (<c>OriginJobId IS NULL</c>) plus templates originally created for
    /// <paramref name="jobId"/>.
    /// </summary>
    /// <param name="jobId">The job to include job-specific templates for.</param>
    public IQueryable<IndividualTemplate> GetVisibleForJob(int jobId) =>
        _context.IndividualTemplates
            .Where(t => t.OriginJobId == null || t.OriginJobId == jobId);
}
