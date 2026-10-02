using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HardwareTemplateBuilder.Tests;

/// <summary>
/// Tests for <see cref="CorruptedRecordSkipService"/>, focused on:
/// <list type="bullet">
///   <item>Exclusive children of a skipped record are cascaded and removed too.</item>
///   <item>A child still referenced by a surviving record is kept, along with its own children.</item>
///   <item>The skipped records are recorded in <c>SyncSkipList</c> so a later sync never re-imports them.</item>
///   <item>A full clean-sync run backs up master, merges local changes, and promotes the clean copy.</item>
/// </list>
/// </summary>
public class CorruptedRecordSkipServiceTests : IDisposable
{
    private readonly string _root;

    public CorruptedRecordSkipServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HtbCleanSyncTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string NewDbPath(string name) => Path.Combine(_root, name);

    private static AppDbContext OpenContext(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);

    [Fact]
    public void CreateCleanMasterAndSync_RemovesExclusiveChild_WhenNothingElseReferencesIt()
    {
        var masterPath = NewDbPath("master.db");
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(masterPath);
        DatabaseInitializer.InitializeAtPath(localPath);

        int manufacturerId, hardwareItemId;
        using (var ctx = OpenContext(masterPath))
        {
            var manufacturer = new Manufacturer { ManufacturerName = "Corrupt Co." };
            var description = new Description { DescriptionText = "Widget" };
            ctx.Manufacturers.Add(manufacturer);
            ctx.Descriptions.Add(description);
            ctx.SaveChanges();

            var hardwareItem = new HardwareItem
            {
                ManufacturerId = manufacturer.Id,
                DescriptionId = description.Id,
                ModelNumber = "M-100"
            };
            ctx.HardwareItems.Add(hardwareItem);
            ctx.SaveChanges();

            manufacturerId = manufacturer.Id;
            hardwareItemId = hardwareItem.Id;
        }

        var recordsToSkip = new[]
        {
            new CorruptedRecordSkipService.SkipCandidate(
                "Manufacturers",
                new Dictionary<string, object?> { ["Id"] = manufacturerId },
                "Test: simulated corruption")
        };

        var result = CorruptedRecordSkipService.CreateCleanMasterAndSync(masterPath, localPath, NewDbPath("backups"), recordsToSkip);

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.SkippedRecordDescriptions, d => d.StartsWith("Manufacturers"));
        Assert.Contains(result.SkippedRecordDescriptions, d => d.StartsWith("HardwareItems"));

        using (var ctx = OpenContext(masterPath))
        {
            Assert.False(ctx.Manufacturers.Any(m => m.Id == manufacturerId));
            Assert.False(ctx.HardwareItems.Any(h => h.Id == hardwareItemId));
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={masterPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"SyncSkipList\";";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public void CreateCleanMasterAndSync_KeepsChild_WhenStillReferencedBySurvivingRecord()
    {
        var masterPath = NewDbPath("master.db");
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(masterPath);
        DatabaseInitializer.InitializeAtPath(localPath);

        int manufacturerId, hardwareItemId;
        using (var ctx = OpenContext(masterPath))
        {
            var manufacturer = new Manufacturer { ManufacturerName = "Corrupt Co." };
            var description = new Description { DescriptionText = "Widget" };
            ctx.Manufacturers.Add(manufacturer);
            ctx.Descriptions.Add(description);
            ctx.SaveChanges();

            var hardwareItem = new HardwareItem
            {
                ManufacturerId = manufacturer.Id,
                DescriptionId = description.Id,
                ModelNumber = "M-200"
            };
            ctx.HardwareItems.Add(hardwareItem);
            ctx.SaveChanges();

            // A surviving Job still references this hardware item via JobHardware, so the
            // HardwareItem is "used by other records" and must be kept even though its
            // Manufacturer is being skipped.
            var customer = new Customer { CustomerName = "Acme" };
            var projectManager = new ProjectManager { ProjectManagerName = "Pat" };
            var userProfile = new UserProfile { UserName = "pat", DefaultTemplateSaveLocation = _root };
            ctx.Customers.Add(customer);
            ctx.ProjectManagers.Add(projectManager);
            ctx.UserProfiles.Add(userProfile);
            ctx.SaveChanges();

            var job = new Job
            {
                JobNumber = "2025-001",
                JobName = "Test Job",
                CustomerId = customer.Id,
                ProjectManagerId = projectManager.Id,
                UserProfileId = userProfile.Id
            };
            ctx.Jobs.Add(job);
            ctx.SaveChanges();

            ctx.JobHardware.Add(new JobHardware { JobId = job.Id, HardwareItemId = hardwareItem.Id });
            ctx.SaveChanges();

            manufacturerId = manufacturer.Id;
            hardwareItemId = hardwareItem.Id;
        }

        var recordsToSkip = new[]
        {
            new CorruptedRecordSkipService.SkipCandidate(
                "Manufacturers",
                new Dictionary<string, object?> { ["Id"] = manufacturerId },
                "Test: simulated corruption")
        };

        var result = CorruptedRecordSkipService.CreateCleanMasterAndSync(masterPath, localPath, NewDbPath("backups"), recordsToSkip);

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.SkippedRecordDescriptions, d => d.StartsWith("Manufacturers"));
        Assert.DoesNotContain(result.SkippedRecordDescriptions, d => d.StartsWith("HardwareItems"));

        using var verifyCtx = OpenContext(masterPath);
        Assert.False(verifyCtx.Manufacturers.Any(m => m.Id == manufacturerId));
        // The hardware item survives because JobHardware (a record not being removed) still
        // references it.
        Assert.True(verifyCtx.HardwareItems.Any(h => h.Id == hardwareItemId));
    }

    [Fact]
    public void CreateCleanMasterAndSync_PromotesCleanCopyAsNewLocalDatabase()
    {
        var masterPath = NewDbPath("master.db");
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(masterPath);
        DatabaseInitializer.InitializeAtPath(localPath);

        int manufacturerId;
        using (var ctx = OpenContext(masterPath))
        {
            var manufacturer = new Manufacturer { ManufacturerName = "Corrupt Co." };
            ctx.Manufacturers.Add(manufacturer);
            ctx.SaveChanges();
            manufacturerId = manufacturer.Id;
        }

        // A change made only on this machine's local copy must still be present after the clean
        // sync, since it merges local changes into the clean copy before promoting it. Use an Id
        // that doesn't collide with the skipped master record's Id (skip-list matching is by
        // primary key, same as the existing tombstone mechanism) by inserting a throwaway row
        // first so autoincrement moves past it.
        using (var ctx = OpenContext(localPath))
        {
            ctx.Manufacturers.Add(new Manufacturer { ManufacturerName = "Placeholder" });
            ctx.SaveChanges();
            ctx.Manufacturers.Add(new Manufacturer { ManufacturerName = "Survives Locally" });
            ctx.SaveChanges();
        }

        var recordsToSkip = new[]
        {
            new CorruptedRecordSkipService.SkipCandidate(
                "Manufacturers",
                new Dictionary<string, object?> { ["Id"] = manufacturerId },
                "Test: simulated corruption")
        };

        var result = CorruptedRecordSkipService.CreateCleanMasterAndSync(masterPath, localPath, NewDbPath("backups"), recordsToSkip);

        Assert.True(result.Success, result.Message);

        using var localCtx = OpenContext(localPath);
        Assert.False(localCtx.Manufacturers.Any(m => m.Id == manufacturerId));
        Assert.True(localCtx.Manufacturers.Any(m => m.ManufacturerName == "Survives Locally"));
    }

    [Fact]
    public void CreateCleanMasterAndSync_ReturnsFailure_WhenNoRecordsSpecified()
    {
        var masterPath = NewDbPath("master.db");
        var localPath = NewDbPath("local.db");
        DatabaseInitializer.InitializeAtPath(masterPath);
        DatabaseInitializer.InitializeAtPath(localPath);

        var result = CorruptedRecordSkipService.CreateCleanMasterAndSync(
            masterPath, localPath, NewDbPath("backups"), Array.Empty<CorruptedRecordSkipService.SkipCandidate>());

        Assert.False(result.Success);
    }
}
