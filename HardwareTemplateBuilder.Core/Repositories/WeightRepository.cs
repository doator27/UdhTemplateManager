using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="Weight"/> entities.</summary>
public class WeightRepository : RepositoryBase<Weight>
{
    /// <inheritdoc/>
    public WeightRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override Weight? FindDuplicate(Weight entity) =>
        _context.Weights.FirstOrDefault(w =>
            w.WeightValue == entity.WeightValue &&
            w.DescriptionId == entity.DescriptionId);
}
