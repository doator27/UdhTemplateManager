using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="Description"/> entities.</summary>
public class DescriptionRepository : RepositoryBase<Description>
{
    /// <inheritdoc/>
    public DescriptionRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override Description? FindDuplicate(Description entity) =>
        _context.Descriptions.FirstOrDefault(d =>
            d.DescriptionText == entity.DescriptionText &&
            d.ParentId == entity.ParentId);
}
