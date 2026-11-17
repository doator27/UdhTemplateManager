using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

// Runs only the new migration's SQL on an empty LocalDB database, then uses the new tables.
var cs = @"Server=(localdb)\MSSQLLocalDB;Database=HtbCiMigrate;Trusted_Connection=True;TrustServerCertificate=True";
using (var ctx = DatabaseInitializer.CreateContextForConnectionString(cs))
{
    ctx.Database.EnsureDeleted();
    ctx.GetService<IRelationalDatabaseCreator>().Create();
    ctx.Database.ExecuteSqlRaw("CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL PRIMARY KEY, [ProductVersion] nvarchar(32) NOT NULL)");
    var script = ctx.GetService<IMigrator>().GenerateScript("20261021182731_AddIsActiveToDescription", "20261117200000_AddPdfImportLearning");
    foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
        if (batch.Trim().Length > 0) ctx.Database.ExecuteSqlRaw(batch);
    Console.WriteLine(script);
}
using (var ctx = DatabaseInitializer.CreateContextForConnectionString(cs))
{
    var g = new HardwareImportGroup { ManufacturerCode = "LC", Catalog = "4040XP REGARM", SourceKey = ImportText.Key("4040XP REGARM"), HardwareItemId = 5, Label = "4040XP REGARM" };
    for (int i = 0; i < 5; i++) HardwareImportLearningService.Record(ctx, new[] { g }, _ => (2, "4040XP RWPA"));
    var m = ctx.PdfImportItemMappings.Single();
    var a = ctx.PdfImportManufacturerAliases.Single();
    Console.WriteLine($"MIGRATE OK: mapping count={m.ConfirmCount} item={m.HardwareItemId} alias {a.Code}->{a.ManufacturerId} x{a.ConfirmCount}");
    if (m.ConfirmCount != 5 || a.ConfirmCount != 5) throw new Exception("counts wrong");
}
