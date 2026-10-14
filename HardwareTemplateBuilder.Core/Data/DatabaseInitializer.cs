using Microsoft.EntityFrameworkCore;
using System;
using System.Data;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Handles creation of the <see cref="AppDbContext"/> pointed at the centrally configured
/// SQL Server instance and applies pending EF Core migrations on first run.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Returns the active SQL Server connection string, as configured via
    /// <see cref="DatabaseLocationService"/>. Throws if no connection has been configured yet
    /// — callers (typically application startup) should catch this and prompt the user via the
    /// database setup UI.
    /// </summary>
    public static string GetConnectionString()
    {
        return "Server=192.168.100.231;Database=listingtoolsdb;Trusted_Connection=True;TrustServerCertificate=True;";
    }

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> pointed at the active SQL Server database.
    /// Transient connection failures (e.g. brief network blips) are retried automatically by
    /// the EF Core execution strategy.
    /// </summary>
    public static AppDbContext CreateContext() => CreateContextForConnectionString(GetConnectionString());

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> for an explicit connection string.
    /// Used by the database setup dialog to validate a connection before it is committed via
    /// <see cref="DatabaseLocationService.SetConfiguredConnectionString"/>, and by the migration
    /// tool.
    /// </summary>
    public static AppDbContext CreateContextForConnectionString(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sqlServerOptions =>
            {
                sqlServerOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                // Avoid translating List<T>.Contains(...) to OPENJSON(...) WITH (...), which fails
                // with "Incorrect syntax near the keyword 'WITH'" on databases whose compatibility
                // level doesn't support OPENJSON (SQL Server 2014 or earlier, i.e. < 130). Falls
                // back to inlining the values as SQL constants instead.
                sqlServerOptions.TranslateParameterizedCollectionsToConstants();
            })
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Ensures the database exists and all pending migrations are applied. Call this on
    /// application startup after the connection string has been resolved.
    /// </summary>
    public static void Initialize()
    {
        using var context = CreateContext();
        EnsureMigrationHistoryBaseline(context);
        context.Database.Migrate();
    }

    /// <summary>
    /// Validates and initializes (creating the schema / applying migrations) the SQL Server
    /// database at <paramref name="connectionString"/>. Used by <c>DatabaseSetupDialog</c> to
    /// verify a connection before committing it.
    /// </summary>
    public static void InitializeAtConnectionString(string connectionString)
    {
        using var context = CreateContextForConnectionString(connectionString);
        EnsureMigrationHistoryBaseline(context);
        context.Database.Migrate();
    }

    /// <summary>
    /// Handles databases whose schema was created out-of-band (e.g. by running the raw
    /// CREATE TABLE script produced by <c>SqlServerScriptExportService</c> directly in SQL
    /// Server Management Studio, instead of letting EF Core migrations create it). In that
    /// scenario the tables already exist but the <c>__EFMigrationsHistory</c> table does not,
    /// so a plain <c>Database.Migrate()</c> call tries to re-run every "create table" migration
    /// and fails with "There is already an object named '...' in the database." every time the
    /// app starts. This stamps the history table with every known migration so
    /// <c>Migrate()</c> treats the existing schema as already up to date instead of
    /// re-applying it.
    /// </summary>
    private static void EnsureMigrationHistoryBaseline(AppDbContext context)
    {
        var db = context.Database;
        if (!db.CanConnect()) return;

        var connection = db.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) connection.Open();
        try
        {
            bool historyTableExists;
            using (var historyCheckCmd = connection.CreateCommand())
            {
                historyCheckCmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'[dbo].[__EFMigrationsHistory]') IS NOT NULL THEN 1 ELSE 0 END";
                historyTableExists = Convert.ToInt32(historyCheckCmd.ExecuteScalar()) == 1;
            }

            if (historyTableExists)
            {
                // The history table exists - only treat it as a real baseline if it actually has
                // rows. An empty history table (e.g. left behind by an interrupted previous run
                // of this same baseline routine) must still be backfilled, otherwise Migrate()
                // keeps failing with "There is already an object named '...'" forever.
                using var rowCountCmd = connection.CreateCommand();
                rowCountCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory]";
                var rowCount = Convert.ToInt32(rowCountCmd.ExecuteScalar());
                if (rowCount > 0) return;
            }

            using (var schemaCheckCmd = connection.CreateCommand())
            {
                // AppSettings is created by the very first migration, so its presence is a
                // reliable signal that the schema already exists.
                schemaCheckCmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'[dbo].[AppSettings]') IS NOT NULL THEN 1 ELSE 0 END";
                var schemaExists = Convert.ToInt32(schemaCheckCmd.ExecuteScalar()) == 1;
                if (!schemaExists) return; // Fresh database - let Migrate() create everything normally.
            }

            if (!historyTableExists)
            {
                using var createHistoryCmd = connection.CreateCommand();
                createHistoryCmd.CommandText = @"CREATE TABLE [dbo].[__EFMigrationsHistory] (
    [MigrationId] nvarchar(150) NOT NULL,
    [ProductVersion] nvarchar(32) NOT NULL,
    CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
);";
                createHistoryCmd.ExecuteNonQuery();
            }

            foreach (var migrationId in context.Database.GetMigrations())
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = @"IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = @migrationId)
INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (@migrationId, @productVersion)";

                var migrationIdParam = insertCmd.CreateParameter();
                migrationIdParam.ParameterName = "@migrationId";
                migrationIdParam.Value = migrationId;
                insertCmd.Parameters.Add(migrationIdParam);

                var productVersionParam = insertCmd.CreateParameter();
                productVersionParam.ParameterName = "@productVersion";
                productVersionParam.Value = "9.0.0";
                insertCmd.Parameters.Add(productVersionParam);

                insertCmd.ExecuteNonQuery();
            }
        }
        finally
        {
            if (shouldClose) connection.Close();
        }
    }
}
