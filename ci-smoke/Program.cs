using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;

// Applies every migration to an empty LocalDB database (what the app does at startup), then
// loads the PDF import knowledge from it.
var cs = @"Server=(localdb)\MSSQLLocalDB;Database=HtbCiMigrate;Trusted_Connection=True;TrustServerCertificate=True";
DatabaseInitializer.InitializeAtConnectionString(cs);
using var ctx = DatabaseInitializer.CreateContextForConnectionString(cs);
var k = HardwareImportKnowledge.Load(ctx);
Console.WriteLine($"MIGRATE OK: mappings={ctx.PdfImportItemMappings.Count()} aliases={ctx.PdfImportManufacturerAliases.Count()} threshold={k.Threshold}");
