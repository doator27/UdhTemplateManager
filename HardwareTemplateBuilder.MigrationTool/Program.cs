using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

if (args.Length < 2)
{
    Console.WriteLine("Usage: HardwareTemplateBuilder.MigrationTool <path-to-sqlite-file> \"<mysql-connection-string>\"");
    return 1;
}


var sqlitePath = args[0];
var mysqlConnectionString = args[1];

if (!File.Exists(sqlitePath))
{
    Console.WriteLine($"SQLite database file not found: {sqlitePath}");
    return 1;
}

var sqliteOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite($"Data Source={sqlitePath}")
    .Options;

var mysqlOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseMySql(mysqlConnectionString, ServerVersion.AutoDetect(mysqlConnectionString))
    .Options;

await using var source = new AppDbContext(sqliteOptions);
await using var target = new AppDbContext(mysqlOptions);

Console.WriteLine("Verifying target database schema is up to date...");
await target.Database.MigrateAsync();

// Open and hold a single physical connection for the duration of the copy so that
// the session-scoped FOREIGN_KEY_CHECKS setting below stays in effect for every
// command. Without this, EF Core's connection pooling can route later commands to
// a different pooled connection where FK checks are still enabled.
await target.Database.OpenConnectionAsync();

Console.WriteLine("Disabling foreign key checks on target database...");
await target.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=0;");

try
{
    // Order does not matter while FK checks are disabled, but tables are listed
    // roughly in dependency order for readability.
    await CopyTableAsync<UserProfile>(source, target, "UserProfiles");
    await CopyTableAsync<Customer>(source, target, "Customers");
    await CopyTableAsync<Manufacturer>(source, target, "Manufacturers");
    await CopyTableAsync<Description>(source, target, "Descriptions");
    await CopyTableAsync<DoorMaterial>(source, target, "DoorMaterials");
    await CopyTableAsync<AppSetting>(source, target, "AppSettings");
    await CopyTableAsync<ProjectManager>(source, target, "ProjectManagers");
    await CopyTableAsync<Job>(source, target, "Jobs");
    await CopyTableAsync<HardwareItem>(source, target, "HardwareItems");
    await CopyTableAsync<IndividualTemplate>(source, target, "IndividualTemplates");
    await CopyTableAsync<HardwareItemTemplate>(source, target, "HardwareItemTemplates");
    await CopyTableAsync<JobRelease>(source, target, "JobReleases");
    await CopyTableAsync<JobHardware>(source, target, "JobHardware");
    await CopyTableAsync<JobTemplateSnapshot>(source, target, "JobTemplateSnapshots");
    await CopyTableAsync<JobAttachment>(source, target, "JobAttachments");
    await CopyBulkAddDraftsAsync(source, target);
    await CopyTableAsync<IgnoredTemplateDuplicate>(source, target, "IgnoredTemplateDuplicates");
}
finally
{
    Console.WriteLine("Re-enabling foreign key checks on target database...");
    await target.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=1;");
}

Console.WriteLine("Migration completed successfully.");
return 0;

static async Task CopyTableAsync<TEntity>(AppDbContext source, AppDbContext target, string tableName)
    where TEntity : class
{
    Console.Write($"Copying {tableName}... ");

    var rows = await source.Set<TEntity>().AsNoTracking().ToListAsync();
    if (rows.Count == 0)
    {
        Console.WriteLine("0 rows.");
        return;
    }

    // Remove any existing rows in the target table so the tool can be re-run safely.
    await target.Set<TEntity>().ExecuteDeleteAsync();

    await target.Set<TEntity>().AddRangeAsync(rows);
    await target.SaveChangesAsync();

    // Detach so the next table's query/save isn't affected by stale tracked entries.
    target.ChangeTracker.Clear();

    Console.WriteLine($"{rows.Count} rows.");
}

static async Task CopyBulkAddDraftsAsync(AppDbContext source, AppDbContext target)
{
    const string tableName = "BulkAddDrafts";
    Console.Write($"Copying {tableName}... ");

    // Read with the raw SavedAt text so rows with corrupted/unparseable timestamps
    // can be skipped individually instead of aborting the entire migration. This
    // table only holds in-progress auto-save scratch data, so skipping a corrupt
    // row is safe (the user's next auto-save will simply replace it).
    var connection = source.Database.GetDbConnection();
    var wasClosed = connection.State != System.Data.ConnectionState.Open;
    if (wasClosed)
    {
        await connection.OpenAsync();
    }

    var drafts = new List<BulkAddDraft>();
    var skipped = 0;

    try
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, JobId, DraftJson, SavedAt FROM BulkAddDrafts;";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var jobId = reader.GetInt32(1);
            var draftJson = reader.GetString(2);
            var savedAtRaw = reader.GetValue(3)?.ToString();

            if (!DateTime.TryParse(
                    savedAtRaw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var savedAt))
            {
                skipped++;
                Console.WriteLine();
                Console.WriteLine($"  Skipping BulkAddDrafts.Id={id} (JobId={jobId}): unparseable SavedAt value '{savedAtRaw}'.");
                continue;
            }

            drafts.Add(new BulkAddDraft
            {
                Id = id,
                JobId = jobId,
                DraftJson = draftJson,
                SavedAt = savedAt
            });
        }
    }
    finally
    {
        if (wasClosed)
        {
            await connection.CloseAsync();
        }
    }

    if (drafts.Count == 0)
    {
        Console.WriteLine("0 rows.");
        return;
    }

    await target.Set<BulkAddDraft>().ExecuteDeleteAsync();
    await target.Set<BulkAddDraft>().AddRangeAsync(drafts);
    await target.SaveChangesAsync();
    target.ChangeTracker.Clear();

    var suffix = skipped > 0 ? $" ({skipped} skipped)" : string.Empty;
    Console.WriteLine($"{drafts.Count} rows.{suffix}");
}
