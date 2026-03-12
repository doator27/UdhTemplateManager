using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;
using System;
using Xunit;

namespace HardwareTemplateBuilder.Tests;

/// <summary>
/// Tests that repository Add methods return existing records instead of
/// creating duplicates when a matching entity already exists.
/// </summary>
public class RedundancyCheckTests : IDisposable
{
    private readonly AppDbContext _context;

    /// <summary>Creates a fresh in-memory SQLite database for each test.</summary>
    public RedundancyCheckTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=:memory:")
            .Options;
        _context = new AppDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    [Fact]
    public void CustomerRepository_Add_ReturnsDuplicateExisting()
    {
        var repo = new CustomerRepository(_context);
        var first = repo.Add(new Customer { CustomerName = "Acme Corp" });
        var second = repo.Add(new Customer { CustomerName = "Acme Corp" });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_context.Customers);
    }

    [Fact]
    public void ManufacturerRepository_Add_ReturnsDuplicateExisting()
    {
        var repo = new ManufacturerRepository(_context);
        var first = repo.Add(new Manufacturer { ManufacturerName = "Schlage" });
        var second = repo.Add(new Manufacturer { ManufacturerName = "Schlage" });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_context.Manufacturers);
    }

    [Fact]
    public void DescriptionRepository_Add_ReturnsDuplicateExisting()
    {
        var repo = new DescriptionRepository(_context);
        var first = repo.Add(new Description { DescriptionText = "Mortise Lockset" });
        var second = repo.Add(new Description { DescriptionText = "Mortise Lockset" });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_context.Descriptions);
    }

    [Fact]
    public void WeightRepository_Add_ReturnsDuplicateExisting()
    {
        // Need a Description first
        var descRepo = new DescriptionRepository(_context);
        var desc = descRepo.Add(new Description { DescriptionText = "Lockset" });

        var repo = new WeightRepository(_context);
        var first = repo.Add(new Weight { WeightValue = "01.001.001", DescriptionId = desc.Id });
        var second = repo.Add(new Weight { WeightValue = "01.001.001", DescriptionId = desc.Id });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_context.Weights);
    }

    [Fact]
    public void HardwareItemRepository_Add_ReturnsDuplicateExisting()
    {
        var mfr = new ManufacturerRepository(_context)
            .Add(new Manufacturer { ManufacturerName = "Schlage" });
        var desc = new DescriptionRepository(_context)
            .Add(new Description { DescriptionText = "Mortise Lockset" });

        var repo = new HardwareItemRepository(_context);
        var first = repo.Add(new HardwareItem
        {
            ManufacturerId = mfr.Id,
            DescriptionId = desc.Id,
            ModelNumber = "L9000"
        });
        var second = repo.Add(new HardwareItem
        {
            ManufacturerId = mfr.Id,
            DescriptionId = desc.Id,
            ModelNumber = "L9000"
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_context.HardwareItems);
    }

    [Fact]
    public void JobRepository_Add_ReturnsDuplicateExisting()
    {
        var customer = new CustomerRepository(_context)
            .Add(new Customer { CustomerName = "Test Customer" });
        var pm = new ProjectManagerRepository(_context)
            .Add(new ProjectManager { ProjectManagerName = "Jane Smith" });
        var user = new UserProfileRepository(_context)
            .Add(new UserProfile { UserName = "jsmith", DefaultTemplateSaveLocation = "/tmp" });

        var repo = new JobRepository(_context);
        var first = repo.Add(new Job
        {
            JobNumber = "2024-001",
            JobName = "Test Job",
            CustomerId = customer.Id,
            ProjectManagerId = pm.Id,
            UserProfileId = user.Id
        });
        var second = repo.Add(new Job
        {
            JobNumber = "2024-001",
            JobName = "Different Name",
            CustomerId = customer.Id,
            ProjectManagerId = pm.Id,
            UserProfileId = user.Id
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_context.Jobs);
    }

    [Fact]
    public void HardwareItemRepository_Search_FiltersByManufacturer()
    {
        var mfr1 = new ManufacturerRepository(_context)
            .Add(new Manufacturer { ManufacturerName = "Schlage" });
        var mfr2 = new ManufacturerRepository(_context)
            .Add(new Manufacturer { ManufacturerName = "Sargent" });
        var desc = new DescriptionRepository(_context)
            .Add(new Description { DescriptionText = "Lockset" });

        var repo = new HardwareItemRepository(_context);
        repo.Add(new HardwareItem { ManufacturerId = mfr1.Id, DescriptionId = desc.Id, ModelNumber = "A1" });
        repo.Add(new HardwareItem { ManufacturerId = mfr2.Id, DescriptionId = desc.Id, ModelNumber = "B1" });

        var results = repo.Search("Schlage", null, null);
        Assert.Single(results);
        Assert.All(results, r => Assert.Equal(mfr1.Id, r.ManufacturerId));
    }

    [Fact]
    public void HardwareItemRepository_Search_SortsByFrequencyDescending()
    {
        var mfr = new ManufacturerRepository(_context)
            .Add(new Manufacturer { ManufacturerName = "Schlage" });
        var desc = new DescriptionRepository(_context)
            .Add(new Description { DescriptionText = "Lockset" });

        var repo = new HardwareItemRepository(_context);
        repo.Add(new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = desc.Id, ModelNumber = "Low", Frequency = 1 });
        repo.Add(new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = desc.Id, ModelNumber = "High", Frequency = 10 });
        repo.Add(new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = desc.Id, ModelNumber = "Mid", Frequency = 5 });

        var results = new System.Collections.Generic.List<HardwareItem>(repo.Search(null, null, null));
        Assert.Equal(10, results[0].Frequency);
        Assert.Equal(5, results[1].Frequency);
        Assert.Equal(1, results[2].Frequency);
    }
}
