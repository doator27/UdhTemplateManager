using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Live test: link history from the August snapshot, real manufacturer websites.
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
var searches = new List<TemplateSearchRequest>
{
    new() { ManufacturerId = MfrId("NGP"), ManufacturerName = "NGP", ModelNumber = "5050C" },
    new() { ManufacturerId = MfrId("ABH"), ManufacturerName = "ABH", ModelNumber = "PT1000" },
    new() { ManufacturerId = MfrId("Ives"), ManufacturerName = "Ives", ModelNumber = "FS436" },
    new() { ManufacturerId = MfrId("Hager"), ManufacturerName = "Hager", ModelNumber = "780-111" },
    new() { ManufacturerId = MfrId("Ives"), ManufacturerName = "Ives", TemplateNumber = "Cut Sheet", ModelNumber = "8400",
            BrokenLink = "https://www.iveshinges.com/catalogcuts/ives-8400.pdf" },
};
foreach (var s in searches)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var progress = new Progress<string>(_ => { });
    try
    {
        var found = await finder.FindAsync(s, progress, null, cts.Token);
        Console.WriteLine($"FIND {s.Summary}: {found.Count} PDF(s) in {sw.Elapsed.TotalSeconds:0}s; sites={string.Join(",", TemplateFinderService.Sites(s.ManufacturerName, new AppDbContext(options).IndividualTemplates.Where(t => t.ManufacturerId == s.ManufacturerId).Select(t => t.OnlineLink!).ToList()))}");
        foreach (var c in found.Take(5)) Console.WriteLine($"FIND    {c.Score,5} {c.DisplayText} | {c.Url}");
    }
    catch (OperationCanceledException) { Console.WriteLine($"FIND {s.Summary}: timed out after {sw.Elapsed.TotalSeconds:0}s"); }
}
