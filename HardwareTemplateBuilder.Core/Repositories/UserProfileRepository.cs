using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="UserProfile"/> entities.</summary>
public class UserProfileRepository : RepositoryBase<UserProfile>
{
    /// <inheritdoc/>
    public UserProfileRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override UserProfile? FindDuplicate(UserProfile entity) =>
        _context.UserProfiles.FirstOrDefault(u => u.UserName == entity.UserName);
}
