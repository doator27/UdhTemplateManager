using System.Globalization;
using System.Text;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Exports the full schema and contents of the active application database to a single
/// flat T-SQL script file that can be opened and run directly in SQL Server Management
/// Studio against an empty/target database. Intended for use from the Maintenance menu.
/// </summary>
public static class SqlServerScriptExportService
{
    /// <summary>
    /// Reads every row from <paramref name="source"/> (the currently configured/active
    /// database) and writes a CREATE TABLE + INSERT script to <paramref name="outputFilePath"/>.
    /// </summary>
    /// <param name="source">An open-able context pointed at the database to export.</param>
    /// <param name="outputFilePath">The destination .sql file path. Overwritten if it already exists.</param>
    /// <returns>The number of tables written.</returns>
    public static async Task<int> ExportAsync(AppDbContext source, string outputFilePath)
    {
        // A SQL Server provider is configured purely so EF Core can script the schema
        // (CREATE TABLE/constraints) for the model. No connection is ever opened against it.
        var schemaOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=HardwareTemplateBuilderExport;Trusted_Connection=True;")
            .Options;

        await using var schema = new AppDbContext(schemaOptions);

        await using var fileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write);
        await using var writer = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine("-- HardwareTemplateBuilder database export");
        writer.WriteLine($"-- Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine("-- Run this script in SQL Server Management Studio against an empty/target database.");
        writer.WriteLine();
        writer.WriteLine(schema.Database.GenerateCreateScript());
        writer.WriteLine();
        writer.WriteLine("GO");
        writer.WriteLine();

        var tableCount = 0;

        // Tables listed in dependency order so FK-referenced rows exist before dependents.
        tableCount += await ExportTableAsync<UserProfile>(source, schema, writer, "UserProfiles");
        tableCount += await ExportTableAsync<Customer>(source, schema, writer, "Customers");
        tableCount += await ExportTableAsync<Manufacturer>(source, schema, writer, "Manufacturers");
        tableCount += await ExportTableAsync<Description>(source, schema, writer, "Descriptions");
        tableCount += await ExportTableAsync<DoorMaterial>(source, schema, writer, "DoorMaterials");
        tableCount += await ExportTableAsync<AppSetting>(source, schema, writer, "AppSettings");
        tableCount += await ExportTableAsync<ProjectManager>(source, schema, writer, "ProjectManagers");
        tableCount += await ExportTableAsync<Job>(source, schema, writer, "Jobs");
        tableCount += await ExportTableAsync<HardwareItem>(source, schema, writer, "HardwareItems");
        tableCount += await ExportTableAsync<IndividualTemplate>(source, schema, writer, "IndividualTemplates");
        tableCount += await ExportTableAsync<HardwareItemTemplate>(source, schema, writer, "HardwareItemTemplates");
        tableCount += await ExportTableAsync<JobRelease>(source, schema, writer, "JobReleases");
        tableCount += await ExportTableAsync<JobHardware>(source, schema, writer, "JobHardware");
        tableCount += await ExportTableAsync<JobTemplateSnapshot>(source, schema, writer, "JobTemplateSnapshots");
        tableCount += await ExportTableAsync<JobAttachment>(source, schema, writer, "JobAttachments");
        tableCount += await ExportTableAsync<BulkAddDraft>(source, schema, writer, "BulkAddDrafts");
        tableCount += await ExportTableAsync<IgnoredTemplateDuplicate>(source, schema, writer, "IgnoredTemplateDuplicates");

        await writer.FlushAsync();

        return tableCount;
    }

    private static async Task<int> ExportTableAsync<TEntity>(AppDbContext source, AppDbContext schema, StreamWriter writer, string tableName)
        where TEntity : class
    {
        var rows = await source.Set<TEntity>().AsNoTracking().ToListAsync();
        if (rows.Count == 0)
        {
            writer.WriteLine($"PRINT N'Importing {tableName}... (0 rows)';");
            writer.WriteLine();
            return 1;
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

        return 1;
    }

    private static string FormatSqlValue(object? value)
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
}
