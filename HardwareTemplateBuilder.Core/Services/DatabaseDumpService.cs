using System.Data;
using System.Text;
using HardwareTemplateBuilder.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Dumps the full contents of every table in the application database to a single
/// human-readable text file. Intended for use from the Maintenance menu as a quick,
/// dependency-free alternative to running <c>mysqldump</c> directly.
/// </summary>
public static class DatabaseDumpService
{
    /// <summary>
    /// Connects to the database using <paramref name="context"/> and writes every table's
    /// contents (column headers followed by tab-delimited row values) to <paramref name="outputFilePath"/>.
    /// </summary>
    /// <param name="context">An open-able context pointed at the database to dump.</param>
    /// <param name="outputFilePath">The destination text file path. Overwritten if it already exists.</param>
    /// <returns>The number of tables written.</returns>
    public static int DumpToTextFile(AppDbContext context, string outputFilePath)
    {
        var tableNames = context.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        using var writer = new StreamWriter(outputFilePath, append: false, Encoding.UTF8);
        var connection = context.Database.GetDbConnection();
        connection.Open();
        try
        {
            foreach (var tableName in tableNames)
            {
                writer.WriteLine($"=== TABLE: {tableName} ===");

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM `{tableName}`";
                using var reader = command.ExecuteReader();

                var columnNames = Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetName)
                    .ToArray();
                writer.WriteLine(string.Join('\t', columnNames));

                var rowCount = 0;
                while (reader.Read())
                {
                    var values = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        values[i] = FormatValue(value);
                    }
                    writer.WriteLine(string.Join('\t', values));
                    rowCount++;
                }

                writer.WriteLine($"({rowCount} row(s))");
                writer.WriteLine();
            }
        }
        finally
        {
            connection.Close();
        }

        return tableNames.Count;
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "NULL";
        var text = value.ToString() ?? string.Empty;
        // Keep output on one line per row: collapse embedded newlines/tabs.
        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\t', ' ');
    }
}
