using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Merges records from an old/backup SQLite database file into the active database without
/// deleting or overwriting anything already present. Only rows whose primary key does not
/// already exist in the target are inserted, so this is safe to run repeatedly (e.g. to catch
/// up a backup database after restoring from an older export).
/// </summary>
public static class SqliteMergeService
{
    /// <summary>Describes how many missing rows were inserted for a single table.</summary>
    public record MergeResult(string TableName, int InsertedCount);

    /// <summary>
    /// Reads every record from the SQLite database at <paramref name="sqlitePath"/> and inserts
    /// any rows whose Id is missing from the corresponding table in <paramref name="target"/>.
    /// </summary>
    /// <param name="sqlitePath">Path to the old/backup SQLite database file.</param>
    /// <param name="target">The active database context to merge missing records into.</param>
    /// <param name="log">Optional callback invoked with progress messages.</param>
    public static async Task<List<MergeResult>> MergeMissingRecordsAsync(
        string sqlitePath,
        AppDbContext target,
        Action<string>? log = null)
    {
        if (!File.Exists(sqlitePath))
            throw new FileNotFoundException("SQLite database file not found.", sqlitePath);

        var sqliteOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqlitePath}")
            .Options;

        await using var source = new AppDbContext(sqliteOptions);

        var isSqlServer = target.Database.IsSqlServer();
        var results = new List<MergeResult>();

        await target.Database.OpenConnectionAsync();
        try
        {
            // Order does not strictly matter for inserts with explicit PK values, but tables are
            // listed in dependency order for readability and to minimize any transient FK issues.
            results.Add(await MergeTableAsync<UserProfile>(source, target, "UserProfiles", isSqlServer, log));
            results.Add(await MergeTableAsync<Customer>(source, target, "Customers", isSqlServer, log));
            results.Add(await MergeTableAsync<Manufacturer>(source, target, "Manufacturers", isSqlServer, log));
            results.Add(await MergeTableAsync<Description>(source, target, "Descriptions", isSqlServer, log));
            results.Add(await MergeTableAsync<DoorMaterial>(source, target, "DoorMaterials", isSqlServer, log));
            results.Add(await MergeTableAsync<AppSetting>(source, target, "AppSettings", isSqlServer, log));
            results.Add(await MergeTableAsync<ProjectManager>(source, target, "ProjectManagers", isSqlServer, log));
            results.Add(await MergeTableAsync<Job>(source, target, "Jobs", isSqlServer, log));
            results.Add(await MergeTableAsync<HardwareItem>(source, target, "HardwareItems", isSqlServer, log));
            results.Add(await MergeTableAsync<IndividualTemplate>(source, target, "IndividualTemplates", isSqlServer, log));
            results.Add(await MergeTableAsync<HardwareItemTemplate>(source, target, "HardwareItemTemplates", isSqlServer, log));
            results.Add(await MergeTableAsync<JobRelease>(source, target, "JobReleases", isSqlServer, log));
            results.Add(await MergeTableAsync<JobHardware>(source, target, "JobHardware", isSqlServer, log));
            results.Add(await MergeTableAsync<JobTemplateSnapshot>(source, target, "JobTemplateSnapshots", isSqlServer, log));
            results.Add(await MergeTableAsync<JobAttachment>(source, target, "JobAttachments", isSqlServer, log));
            results.Add(await MergeTableAsync<IgnoredTemplateDuplicate>(source, target, "IgnoredTemplateDuplicates", isSqlServer, log));
        }
        finally
        {
            await target.Database.CloseConnectionAsync();
        }

        return results;
    }

    private static async Task<MergeResult> MergeTableAsync<TEntity>(
        AppDbContext source,
        AppDbContext target,
        string tableName,
        bool isSqlServer,
        Action<string>? log)
        where TEntity : class
    {
        log?.Invoke($"Checking {tableName}...");

        var entityType = target.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Entity type '{typeof(TEntity).Name}' not found in model.");

        var primaryKey = entityType.FindPrimaryKey()?.Properties.FirstOrDefault()
            ?? throw new InvalidOperationException($"No primary key found for '{typeof(TEntity).Name}'.");

        var pkProperty = primaryKey.PropertyInfo
            ?? throw new InvalidOperationException($"Primary key for '{typeof(TEntity).Name}' has no CLR property.");

        var sourceRows = await source.Set<TEntity>().AsNoTracking().ToListAsync();
        if (sourceRows.Count == 0)
        {
            log?.Invoke($"  {tableName}: 0 rows in backup file.");
            return new MergeResult(tableName, 0);
        }

        var existingIds = (await target.Set<TEntity>().AsNoTracking().ToListAsync())
            .Select(e => pkProperty.GetValue(e))
            .ToHashSet();

        var missing = sourceRows
            .Where(r => !existingIds.Contains(pkProperty.GetValue(r)))
            .ToList();

        if (missing.Count == 0)
        {
            log?.Invoke($"  {tableName}: no missing records.");
            return new MergeResult(tableName, 0);
        }

        var hasIdentityColumn = primaryKey.ValueGenerated == ValueGenerated.OnAdd;

        if (isSqlServer && hasIdentityColumn)
        {
            await target.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] ON;");
        }

        try
        {
            await target.Set<TEntity>().AddRangeAsync(missing);
            await target.SaveChangesAsync();
        }
        finally
        {
            if (isSqlServer && hasIdentityColumn)
            {
                await target.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] OFF;");
            }

            target.ChangeTracker.Clear();
        }

        log?.Invoke($"  {tableName}: inserted {missing.Count} missing record(s).");
        return new MergeResult(tableName, missing.Count);
    }
}
