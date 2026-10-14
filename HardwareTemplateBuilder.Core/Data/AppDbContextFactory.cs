using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Design-time factory for creating <see cref="AppDbContext"/> instances during EF migrations.
/// Uses a placeholder local connection string purely to let the SQL Server provider generate
/// migrations without requiring a reachable server — set the <c>HTB_DESIGNTIME_CONNECTION</c>
/// environment variable to point at a real server if schema-dependent design-time behavior is
/// ever needed.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc/>
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            System.Environment.GetEnvironmentVariable("HTB_DESIGNTIME_CONNECTION")
            ?? "Server=localhost;Database=hardware_templates_designtime;Trusted_Connection=True;TrustServerCertificate=True;";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new AppDbContext(options);
    }
}
