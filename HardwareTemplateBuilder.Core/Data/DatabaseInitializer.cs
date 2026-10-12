using Microsoft.EntityFrameworkCore;
using System;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Handles creation of the <see cref="AppDbContext"/> pointed at the centrally configured
/// MySQL server and applies pending EF Core migrations on first run.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// The MySQL server version this application targets. Used by the EF Core provider to
    /// select SQL syntax appropriate for the server without requiring a live connection at
    /// design time.
    /// </summary>
    public static readonly ServerVersion TargetServerVersion = new MySqlServerVersion(new Version(8, 0, 0));

    /// <summary>
    /// Returns the active MySQL connection string, as configured via
    /// <see cref="DatabaseLocationService"/>. Throws if no connection has been configured yet
    /// — callers (typically application startup) should catch this and prompt the user via the
    /// database setup UI.
    /// </summary>
    public static string GetConnectionString()
    {
        var configured = DatabaseLocationService.GetConfiguredConnectionString();
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                "No MySQL connection has been configured. Use the database setup dialog to configure one.");
        return configured;
    }

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> pointed at the active MySQL database.
    /// Transient connection failures (e.g. brief network blips) are retried automatically by
    /// the EF Core execution strategy.
    /// </summary>
    public static AppDbContext CreateContext() => CreateContextForConnectionString(GetConnectionString());

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> for an explicit connection string.
    /// Used by the database setup dialog to validate a connection before it is committed via
    /// <see cref="DatabaseLocationService.SetConfiguredConnectionString"/>, and by the SQLite to
    /// MySQL migration tool.
    /// </summary>
    public static AppDbContext CreateContextForConnectionString(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, TargetServerVersion, mySqlOptions =>
                mySqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null))
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
        context.Database.Migrate();
    }

    /// <summary>
    /// Validates and initializes (creating the schema / applying migrations) the MySQL database
    /// at <paramref name="connectionString"/>. Used by <c>DatabaseSetupDialog</c> to verify a
    /// connection before committing it.
    /// </summary>
    public static void InitializeAtConnectionString(string connectionString)
    {
        using var context = CreateContextForConnectionString(connectionString);
        context.Database.Migrate();
    }
}
