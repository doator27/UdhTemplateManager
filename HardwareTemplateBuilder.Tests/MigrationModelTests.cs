using HardwareTemplateBuilder.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>
/// Guards the startup migration: the app runs <c>Database.Migrate()</c> on launch, and EF Core 9
/// refuses to migrate when the model has changes no migration covers, which would stop the app
/// from opening. This compares the model with the migrations snapshot (no database needed).
/// </summary>
public class MigrationModelTests
{
    [Fact]
    public void Model_HasNoChangesMissingFromTheMigrations()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=HtbModelCheck;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var ctx = new AppDbContext(options);
        Assert.False(ctx.Database.HasPendingModelChanges(), "The EF model differs from the migrations snapshot.");
    }
}
