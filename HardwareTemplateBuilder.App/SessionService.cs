using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.App;

/// <summary>
/// Holds the user profile chosen at startup for the lifetime of the current process.
/// All operations that need to know "who is working" read from here.
/// </summary>
public static class SessionService
{
    /// <summary>Gets or sets the active user profile for this session.</summary>
    public static UserProfile? ActiveUserProfile { get; set; }
}
