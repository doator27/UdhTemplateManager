using System.Globalization;
using System.Text;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

if (args.Length < 2)
{
    Console.WriteLine("Usage: HardwareTemplateBuilder.MigrationTool <path-to-sqlite-file> \"<target-connection-string>\" [mysql|sqlserver]");
    Console.WriteLine("  provider defaults to 'mysql' when omitted.");
    Console.WriteLine();
    Console.WriteLine("Usage (flat-file export for SQL Server Management Studio):");
    Console.WriteLine("  HardwareTemplateBuilder.MigrationTool <path-to-sqlite-file> <output-script.sql> sqlserver-script");
    return 1;
}

var sqlitePath = args[0];
var targetConnectionString = args[1];
var provider = args.Length >= 3 ? args[2].Trim().ToLowerInvariant() : "mysql";

if (provider is not ("mysql" or "sqlserver" or "sqlserver-script"))
{
    Console.WriteLine($"Unsupported provider '{provider}'. Expected 'mysql', 'sqlserver', or 'sqlserver-script'.");
    return 1;
}

if (!File.Exists(sqlitePath))
{
    Console.WriteLine($"SQLite database file not found: {sqlitePath}");
    return 1;
}

if (provider == "sqlserver-script")
{
    return await ExportToSqlScriptAsync(sqlitePath, targetConnectionString);
}

var sqliteOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite($"Data Source={sqlitePath}")
    .Options;

var targetOptionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
if (provider == "sqlserver")
{
    targetOptionsBuilder.UseSqlServer(targetConnectionString);
}
else
{
    targetOptionsBuilder.UseMySql(targetConnectionString, ServerVersion.AutoDetect(targetConnectionString));
}

var targetOptions = targetOptionsBuilder.Options;

await using var source = new AppDbContext(sqliteOptions);
await using var target = new AppDbContext(targetOptions);

Console.WriteLine("Verifying target database schema is up to date...");
await target.Database.MigrateAsync();

// Open and hold a single physical connection for the duration of the copy so that
// the session-scoped FOREIGN_KEY_CHECKS setting below stays in effect for every
// command. Without this, EF Core's connection pooling can route later commands to
// a different pooled connection where FK checks are still enabled.
await target.Database.OpenConnectionAsync();

Console.WriteLine("Disabling foreign key checks on target database...");
if (provider == "sqlserver")
{
    await target.Database.ExecuteSqlRawAsync("EXEC sp_MSForEachTable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL';");
}
else
{
    await target.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=0;");
}

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
    if (provider == "sqlserver")
    {
        await target.Database.ExecuteSqlRawAsync("EXEC sp_MSForEachTable 'ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL';");
    }
    else
    {
        await target.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=1;");
    }
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

static async Task<int> ExportToSqlScriptAsync(string sqlitePath, string outputPath)
{
    var sqliteOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={sqlitePath}")
        .Options;

    // A SQL Server provider is configured purely so EF Core can script the schema
    // (CREATE TABLE/constraints) for the model. No connection is ever opened against it.
    var schemaOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=(local);Database=HardwareTemplateBuilderExport;Trusted_Connection=True;")
        .Options;

    await using var source = new AppDbContext(sqliteOptions);
    await using var schema = new AppDbContext(schemaOptions);

    Console.WriteLine($"Generating SQL Server script: {outputPath}");

    await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
    await using var writer = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    writer.WriteLine("-- HardwareTemplateBuilder database export");
    writer.WriteLine($"-- Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    writer.WriteLine("-- Run this script in SQL Server Management Studio against an empty/target database.");
    writer.WriteLine();
    writer.WriteLine(schema.Database.GenerateCreateScript());
    writer.WriteLine();
    writer.WriteLine("GO");
    writer.WriteLine();

    // Tables listed in dependency order so FK-referenced rows exist before dependents.
    await ExportTableAsync<UserProfile>(source, schema, writer, "UserProfiles");
    await ExportTableAsync<Customer>(source, schema, writer, "Customers");
    await ExportTableAsync<Manufacturer>(source, schema, writer, "Manufacturers");
    await ExportTableAsync<Description>(source, schema, writer, "Descriptions");
    await ExportTableAsync<DoorMaterial>(source, schema, writer, "DoorMaterials");
    await ExportTableAsync<AppSetting>(source, schema, writer, "AppSettings");
    await ExportTableAsync<ProjectManager>(source, schema, writer, "ProjectManagers");
    await ExportTableAsync<Job>(source, schema, writer, "Jobs");
    await ExportTableAsync<HardwareItem>(source, schema, writer, "HardwareItems");
    await ExportTableAsync<IndividualTemplate>(source, schema, writer, "IndividualTemplates");
    await ExportTableAsync<HardwareItemTemplate>(source, schema, writer, "HardwareItemTemplates");
    await ExportTableAsync<JobRelease>(source, schema, writer, "JobReleases");
    await ExportTableAsync<JobHardware>(source, schema, writer, "JobHardware");
    await ExportTableAsync<JobTemplateSnapshot>(source, schema, writer, "JobTemplateSnapshots");
    await ExportTableAsync<JobAttachment>(source, schema, writer, "JobAttachments");
    await ExportTableAsync<BulkAddDraft>(source, schema, writer, "BulkAddDrafts");
    await ExportTableAsync<IgnoredTemplateDuplicate>(source, schema, writer, "IgnoredTemplateDuplicates");

    await writer.FlushAsync();

    Console.WriteLine("Export completed successfully.");
    return 0;
}

static async Task ExportTableAsync<TEntity>(AppDbContext source, AppDbContext schema, StreamWriter writer, string tableName)
    where TEntity : class
{
    Console.Write($"Exporting {tableName}... ");

    var rows = await source.Set<TEntity>().AsNoTracking().ToListAsync();
    if (rows.Count == 0)
    {
        Console.WriteLine("0 rows.");
        return;
    }

    var entityType = schema.Model.FindEntityType(typeof(TEntity))
        ?? throw new InvalidOperationException($"Entity type '{typeof(TEntity).Name}' not found in model.");

    var properties = entityType.GetProperties()
        .Where(p => p.PropertyInfo is not null)
        .ToList();

    var hasIdentityColumn = properties.Any(p => p.ValueGenerated == ValueGenerated.OnAdd);

    writer.WriteLine($"PRINT N'Importing {tableName}...';");
    if (hasIdentityColumn)
    {
        writer.WriteLine($"SET IDENTITY_INSERT [{tableName}] ON;");
    }

    var columnList = string.Join(", ", properties.Select(p => $"[{p.Name}]"));

    foreach (var row in rows)
    {
        var valueList = string.Join(", ", properties.Select(p => FormatSqlValue(p.PropertyInfo!.GetValue(row))));
        writer.WriteLine($"INSERT INTO [{tableName}] ({columnList}) VALUES ({valueList});");
    }

    if (hasIdentityColumn)
    {
        writer.WriteLine($"SET IDENTITY_INSERT [{tableName}] OFF;");
    }

    writer.WriteLine("GO");
    writer.WriteLine();

    Console.WriteLine($"{rows.Count} rows.");
}

static string FormatSqlValue(object? value)
{
    return value switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float or double or decimal
            => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        string s => $"N'{s.Replace("'", "''")}'",
        Enum e => Convert.ToInt64(e).ToString(CultureInfo.InvariantCulture),
        _ => $"N'{value.ToString()!.Replace("'", "''")}'"
    };
}
