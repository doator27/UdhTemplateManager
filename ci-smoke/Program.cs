using HardwareTemplateBuilder.Core.Services.Import;
using Microsoft.Data.Sqlite;

// Matches the test excerpts against the August database snapshot (hardware_templates.db).
using var db = new SqliteConnection("Data Source=hardware_templates.db;Mode=ReadOnly");
db.Open();
List<T> Query<T>(string sql, Func<SqliteDataReader, T> map)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = sql;
    using var r = cmd.ExecuteReader(); var list = new List<T>();
    while (r.Read()) list.Add(map(r));
    return list;
}
var mfrs = Query("select Id, ManufacturerName from Manufacturers", r => (r.GetInt32(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
var items = Query("select Id, ManufacturerId, DescriptionId, ModelNumber from HardwareItems where IsActive = 1",
    r => new ImportItemInfo(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3))).ToDictionary(i => i.Id);
var labels = Query("select HardwareItemId, CustomDescription, count(*) from JobHardware where CustomDescription is not null and CustomDescription <> '' group by HardwareItemId, CustomDescription",
        r => new ImportLabelUse(r.GetInt32(0), r.GetString(1), ImportText.Key(r.GetString(1)), r.GetInt32(2)))
    .Where(l => items.ContainsKey(l.HardwareItemId) && l.Key.Length > 0).GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.ToList());
var k = new HardwareImportKnowledge { Manufacturers = mfrs, Items = items, LabelsByKey = labels, Threshold = 5 };

foreach (var file in new[] { "schedule.txt", "costing.txt", "scan_ocr.txt" })
{
    var lines = File.ReadAllLines(Path.Combine("HardwareTemplateBuilder.Tests", "TestData", "PdfImport", file));
    var parsed = HardwarePdfParser.Parse(new[] { (IReadOnlyList<string>)lines });
    var groups = HardwareImportMatcher.Match(parsed.Lines, k);
    Console.WriteLine($"=== {file}: {parsed.Lines.Count} lines, {groups.Count} distinct, formats: {string.Join(",", parsed.Formats)}");
    foreach (var g in groups)
    {
        var item = g.HardwareItemId is int id && items.TryGetValue(id, out var it) ? $"{mfrs[it.ManufacturerId]} | {it.ModelNumber}" : "-";
        Console.WriteLine($"RES {g.Status,-11} {g.ManufacturerCode,-5}->{(g.ManufacturerId is int m ? mfrs[m] : "?"),-10} [{g.Catalog}] => {item} [{g.Label}]");
    }
}
