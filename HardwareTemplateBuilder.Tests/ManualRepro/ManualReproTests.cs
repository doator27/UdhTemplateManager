using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using HardwareTemplateBuilder.Core.Data;
using Xunit;
using Xunit.Abstractions;

namespace HardwareTemplateBuilder.Tests.ManualRepro;

public class ManualReproTests
{
    private readonly ITestOutputHelper _output;

    public ManualReproTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RunSyncThenCleanSync_AgainstRealDbCopies()
    {
        var masterPath = @"C:\Temp\HtbDebug\master.db";
        var localPath = @"C:\Temp\HtbDebug\local.db";
        var backupDir = @"C:\Temp\HtbDebug\backups";

        if (!File.Exists(masterPath) || !File.Exists(localPath))
        {
            _output.WriteLine("Skipping - db copies not present.");
            return;
        }

        var sw = Stopwatch.StartNew();
        _output.WriteLine("Running MasterSyncService.Sync...");
        var result = MasterSyncService.Sync(masterPath, localPath);
        sw.Stop();
        _output.WriteLine($"Sync result: {result} (took {sw.Elapsed.TotalSeconds:0.0}s)");
        _output.WriteLine($"LastError: {MasterSyncService.LastError}");

        var failed = MasterSyncService.LastFailedRecords;
        _output.WriteLine($"Failed records: {failed.Count}");
        foreach (var f in failed.Take(10))
        {
            var pk = string.Join(", ", f.PrimaryKey.Select(kv => $"{kv.Key}={kv.Value}"));
            _output.WriteLine($"  {f.TableName} ({pk}): {f.Reason}");
        }
    }
}
