using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("finder").Options;
using (var seed = new AppDbContext(options))
using (var db = new SqliteConnection("Data Source=hardware_templates.db;Mode=ReadOnly"))
{
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "select Id, ManufacturerName from Manufacturers";
    using (var r = cmd.ExecuteReader()) while (r.Read()) seed.Manufacturers.Add(new Manufacturer { Id = r.GetInt32(0), ManufacturerName = r.GetString(1) });
    cmd.CommandText = "select Id, ManufacturerId, TemplateNumber, OnlineLink from IndividualTemplates where OnlineLink like 'http%'";
    using (var r = cmd.ExecuteReader()) while (r.Read())
        seed.IndividualTemplates.Add(new IndividualTemplate { Id = r.GetInt32(0), ManufacturerId = r.GetInt32(1), TemplateNumber = r.GetString(2), OnlineLink = r.GetString(3), PagesToPrint = "1", NumPages = 1 });
    seed.SaveChanges();
}
int MfrId(string name) { using var c = new AppDbContext(options); return c.Manufacturers.First(m => m.ManufacturerName == name).Id; }

using var http = TemplateFinderService.CreateHttpClient();
var finder = new TemplateFinderService(http, () => new AppDbContext(options));
foreach (var s in new List<TemplateSearchRequest>
{
    new() { ManufacturerId = MfrId("Ives"), ManufacturerName = "Ives", ModelNumber = "FS436" },
    new() { ManufacturerId = MfrId("Trimco"), ManufacturerName = "Trimco", ModelNumber = "1270" },
    new() { ManufacturerId = MfrId("Detex"), ManufacturerName = "Detex", ModelNumber = "ECL-230D" },
    new() { ManufacturerId = MfrId("Don Jo"), ManufacturerName = "Don Jo", ModelNumber = "PL-4" },
})
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        var found = await finder.FindAsync(s, null, null, cts.Token);
        Console.WriteLine($"FIND {s.Summary}: {found.Count} PDF(s) in {sw.Elapsed.TotalSeconds:0}s; sites={string.Join(",", finder.SitesFor(s))}; blocked={string.Join(",", finder.BlockedSites)}");
        foreach (var c in found.Take(4)) Console.WriteLine($"FIND    {c.Score,5} {c.DisplayText} | {c.Url}");
    }
    catch (OperationCanceledException) { Console.WriteLine($"FIND {s.Summary}: timed out after {sw.Elapsed.TotalSeconds:0}s"); }
}
