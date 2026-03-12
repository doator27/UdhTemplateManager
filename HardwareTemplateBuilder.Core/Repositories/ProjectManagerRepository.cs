using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="ProjectManager"/> entities.</summary>
public class ProjectManagerRepository : RepositoryBase<ProjectManager>
{
    /// <inheritdoc/>
    public ProjectManagerRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override ProjectManager? FindDuplicate(ProjectManager entity) =>
        _context.ProjectManagers.FirstOrDefault(pm => pm.ProjectManagerName == entity.ProjectManagerName);
}
