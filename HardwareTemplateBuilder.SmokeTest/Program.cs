using System;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

Console.WriteLine("=== Hardware Template Builder — Smoke Test ===");
Console.WriteLine();

// Initialize database
Console.WriteLine("Initializing database...");
DatabaseInitializer.Initialize();
Console.WriteLine($"Database at: {DatabaseInitializer.GetDatabasePath()}");
Console.WriteLine();

using var context = DatabaseInitializer.CreateContext();

// --- Insert one record into each table ---
Console.WriteLine("Inserting test records...");

// UserProfile
var user = new UserProfile { UserName = "TestUser", DefaultTemplateSaveLocation = "/tmp/templates" };
context.UserProfiles.Add(user);

// Customer
var customer = new Customer { CustomerName = "Test Customer Inc." };
context.Customers.Add(customer);

// Manufacturer
var manufacturer = new Manufacturer { ManufacturerName = "Test Mfr Co." };
context.Manufacturers.Add(manufacturer);

var description = new Description { DescriptionText = "Test Lockset", SortOrder = 1 };
context.Descriptions.Add(description);

// ProjectManager
var pm = new ProjectManager { ProjectManagerName = "John Doe" };
context.ProjectManagers.Add(pm);

context.SaveChanges();
Console.WriteLine("  Saved: UserProfile, Customer, Manufacturer, Description, ProjectManager");

// DoorMaterial — already seeded, just read
var hollowMetal = context.DoorMaterials.First(dm => dm.Material == "Hollow Metal");
var wood = context.DoorMaterials.First(dm => dm.Material == "Wood");
Console.WriteLine($"  Seeded DoorMaterials: '{hollowMetal.Material}', '{wood.Material}'");

// HardwareItem (needs Manufacturer + Description)
var hardwareItem = new HardwareItem
{
    ManufacturerId = manufacturer.Id,
    DescriptionId = description.Id,
    ModelNumber = "TEST-001",
    Remarks = "Smoke test item",
    Frequency = 0,
    IsActive = true
};
context.HardwareItems.Add(hardwareItem);

// IndividualTemplate (needs Manufacturer + Description + DoorMaterial; weight is on Description)
var template = new IndividualTemplate
{
    ManufacturerId = manufacturer.Id,
    DescriptionId = description.Id,
    TemplateNumber = "T-001",
    NumPages = 2,
    PagesToPrint = "1-2",
    PagesToRotate = null,
    RotationDirection = 0,
    DoorMaterialId = hollowMetal.Id,
    OnlineLink = null,
    LocalLink = null
};
context.IndividualTemplates.Add(template);

context.SaveChanges();
Console.WriteLine("  Saved: HardwareItem, IndividualTemplate");

// HardwareItemTemplate (junction)
var hit = new HardwareItemTemplate
{
    HardwareItemId = hardwareItem.Id,
    IndividualTemplateId = template.Id
};
context.HardwareItemTemplates.Add(hit);

// Job (needs Customer + ProjectManager + UserProfile)
var job = new Job
{
    JobNumber = "2024-SMOKE",
    JobName = "Smoke Test Job",
    CustomerId = customer.Id,
    ProjectManagerId = pm.Id,
    UserProfileId = user.Id
};
context.Jobs.Add(job);

context.SaveChanges();
Console.WriteLine("  Saved: HardwareItemTemplate, Job");

// JobHardware (junction)
var jobHardware = new JobHardware
{
    JobId = job.Id,
    HardwareItemId = hardwareItem.Id
};
context.JobHardware.Add(jobHardware);

// JobTemplateSnapshot
var snapshot = new JobTemplateSnapshot
{
    JobId = job.Id,
    IndividualTemplateId = template.Id,
    SnapshotLocalLink = "/tmp/templates/TestMfrCo_T-001.pdf",
    SnapshotDate = DateTime.UtcNow,
    PagesToPrint = "1-2",
    PagesToRotate = null,
    RotationDirection = 0
};
context.JobTemplateSnapshots.Add(snapshot);

context.SaveChanges();
Console.WriteLine("  Saved: JobHardware, JobTemplateSnapshot");
Console.WriteLine();

// --- Read back each record ---
Console.WriteLine("Reading back records...");
Console.WriteLine($"  UserProfile: {context.UserProfiles.Find(user.Id)?.UserName}");
Console.WriteLine($"  Customer: {context.Customers.Find(customer.Id)?.CustomerName}");
Console.WriteLine($"  Manufacturer: {context.Manufacturers.Find(manufacturer.Id)?.ManufacturerName}");
Console.WriteLine($"  Description: {context.Descriptions.Find(description.Id)?.DescriptionText} (sort: {context.Descriptions.Find(description.Id)?.SortOrder})");
Console.WriteLine($"  DoorMaterial(1): {context.DoorMaterials.Find(1)?.Material}");
Console.WriteLine($"  ProjectManager: {context.ProjectManagers.Find(pm.Id)?.ProjectManagerName}");
Console.WriteLine($"  HardwareItem: {context.HardwareItems.Find(hardwareItem.Id)?.ModelNumber}");
Console.WriteLine($"  IndividualTemplate: {context.IndividualTemplates.Find(template.Id)?.TemplateNumber}");
Console.WriteLine($"  HardwareItemTemplate: HardwareItemId={context.HardwareItemTemplates.Find(hit.Id)?.HardwareItemId}");
Console.WriteLine($"  Job: {context.Jobs.Find(job.Id)?.JobNumber}");
Console.WriteLine($"  JobHardware: JobId={context.JobHardware.Find(jobHardware.Id)?.JobId}");
Console.WriteLine($"  JobTemplateSnapshot: SnapshotDate={context.JobTemplateSnapshots.Find(snapshot.Id)?.SnapshotDate}");
Console.WriteLine();

// --- Delete all test records ---
Console.WriteLine("Cleaning up test records...");
context.JobTemplateSnapshots.Remove(snapshot);
context.JobHardware.Remove(jobHardware);
context.Jobs.Remove(job);
context.HardwareItemTemplates.Remove(hit);
context.IndividualTemplates.Remove(template);
context.HardwareItems.Remove(hardwareItem);
context.ProjectManagers.Remove(pm);
context.Descriptions.Remove(description);
context.Manufacturers.Remove(manufacturer);
context.Customers.Remove(customer);
context.UserProfiles.Remove(user);
context.SaveChanges();
Console.WriteLine("  All test records deleted.");
Console.WriteLine();
Console.WriteLine("=== Smoke Test PASSED ===");
