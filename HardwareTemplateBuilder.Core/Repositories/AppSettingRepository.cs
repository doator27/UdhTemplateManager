using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Repository for reading and updating <see cref="AppSetting"/> records.
/// </summary>
public class AppSettingRepository : RepositoryBase<AppSetting>
{
    /// <summary>Initializes a new <see cref="AppSettingRepository"/>.</summary>
    public AppSettingRepository(AppDbContext context) : base(context) { }

    /// <summary>
    /// Returns the value for the given key, or an empty string if the key does not exist.
    /// </summary>
    /// <param name="key">The setting key to look up.</param>
    public string GetValue(string key) =>
        _context.AppSettings.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

    /// <summary>
    /// Sets the value for the given key. If a record with that key already exists it is
    /// updated in place; otherwise a new record is inserted.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The new value to store.</param>
    public void SetValue(string key, string value)
    {
        var existing = _context.AppSettings.FirstOrDefault(s => s.Key == key);
        if (existing != null)
        {
            existing.Value = value;
            Update(existing);
        }
        else
        {
            Add(new AppSetting { Key = key, Value = value });
        }
    }

    /// <inheritdoc/>
    protected override AppSetting? FindDuplicate(AppSetting entity) =>
        _context.AppSettings.FirstOrDefault(s => s.Key == entity.Key);
}
