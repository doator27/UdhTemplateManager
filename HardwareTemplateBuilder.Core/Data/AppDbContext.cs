using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Entity Framework Core database context for the Hardware Template Builder application.
/// Manages all entity sets and configures relationships and constraints.
/// </summary>
public class AppDbContext : DbContext
{
    /// <summary>Initializes a new instance of <see cref="AppDbContext"/> with the specified options.</summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// <summary>Gets or sets the user profiles table.</summary>
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    /// <summary>Gets or sets the customers table.</summary>
    public DbSet<Customer> Customers => Set<Customer>();

    /// <summary>Gets or sets the manufacturers table.</summary>
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();

    /// <summary>Gets or sets the descriptions table.</summary>
    public DbSet<Description> Descriptions => Set<Description>();

    /// <summary>Gets or sets the door materials table.</summary>
    public DbSet<DoorMaterial> DoorMaterials => Set<DoorMaterial>();

    /// <summary>Gets or sets the weights table.</summary>
    public DbSet<Weight> Weights => Set<Weight>();

    /// <summary>Gets or sets the project managers table.</summary>
    public DbSet<ProjectManager> ProjectManagers => Set<ProjectManager>();

    /// <summary>Gets or sets the hardware items table.</summary>
    public DbSet<HardwareItem> HardwareItems => Set<HardwareItem>();

    /// <summary>Gets or sets the individual templates table.</summary>
    public DbSet<IndividualTemplate> IndividualTemplates => Set<IndividualTemplate>();

    /// <summary>Gets or sets the hardware item to template junction table.</summary>
    public DbSet<HardwareItemTemplate> HardwareItemTemplates => Set<HardwareItemTemplate>();

    /// <summary>Gets or sets the jobs table.</summary>
    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>Gets or sets the job to hardware item junction table.</summary>
    public DbSet<JobHardware> JobHardware => Set<JobHardware>();

    /// <summary>Gets or sets the job template snapshots table.</summary>
    public DbSet<JobTemplateSnapshot> JobTemplateSnapshots => Set<JobTemplateSnapshot>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // DoorMaterial: constrain Material to only "Hollow Metal" or "Wood"
        modelBuilder.Entity<DoorMaterial>()
            .ToTable(t => t.HasCheckConstraint("CK_DoorMaterial_Material",
                "\"Material\" IN ('Hollow Metal', 'Wood')"));

        // Seed the two valid DoorMaterial values
        modelBuilder.Entity<DoorMaterial>().HasData(
            new DoorMaterial { Id = 1, Material = "Hollow Metal" },
            new DoorMaterial { Id = 2, Material = "Wood" }
        );

        // HardwareItem: default Frequency to 0
        modelBuilder.Entity<HardwareItem>()
            .Property(h => h.Frequency)
            .HasDefaultValue(0);

        // JobTemplateSnapshot: default SnapshotDate to current UTC time
        modelBuilder.Entity<JobTemplateSnapshot>()
            .Property(s => s.SnapshotDate)
            .HasDefaultValueSql("datetime('now')");

        // Weight → Description FK
        modelBuilder.Entity<Weight>()
            .HasOne(w => w.Description)
            .WithMany(d => d.Weights)
            .HasForeignKey(w => w.DescriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // HardwareItem → Manufacturer
        modelBuilder.Entity<HardwareItem>()
            .HasOne(h => h.Manufacturer)
            .WithMany(m => m.HardwareItems)
            .HasForeignKey(h => h.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // HardwareItem → Description
        modelBuilder.Entity<HardwareItem>()
            .HasOne(h => h.Description)
            .WithMany(d => d.HardwareItems)
            .HasForeignKey(h => h.DescriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // IndividualTemplate → Manufacturer
        modelBuilder.Entity<IndividualTemplate>()
            .HasOne(t => t.Manufacturer)
            .WithMany(m => m.Templates)
            .HasForeignKey(t => t.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // IndividualTemplate → Description
        modelBuilder.Entity<IndividualTemplate>()
            .HasOne(t => t.Description)
            .WithMany(d => d.Templates)
            .HasForeignKey(t => t.DescriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // IndividualTemplate → Weight
        modelBuilder.Entity<IndividualTemplate>()
            .HasOne(t => t.Weight)
            .WithMany(w => w.Templates)
            .HasForeignKey(t => t.WeightId)
            .OnDelete(DeleteBehavior.Restrict);

        // IndividualTemplate → DoorMaterial
        modelBuilder.Entity<IndividualTemplate>()
            .HasOne(t => t.DoorMaterial)
            .WithMany(dm => dm.Templates)
            .HasForeignKey(t => t.DoorMaterialId)
            .OnDelete(DeleteBehavior.Restrict);

        // HardwareItemTemplate (junction)
        modelBuilder.Entity<HardwareItemTemplate>()
            .HasOne(hit => hit.HardwareItem)
            .WithMany(h => h.HardwareItemTemplates)
            .HasForeignKey(hit => hit.HardwareItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<HardwareItemTemplate>()
            .HasOne(hit => hit.IndividualTemplate)
            .WithMany(t => t.HardwareItemTemplates)
            .HasForeignKey(hit => hit.IndividualTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        // Job → Customer
        modelBuilder.Entity<Job>()
            .HasOne(j => j.Customer)
            .WithMany(c => c.Jobs)
            .HasForeignKey(j => j.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Job → ProjectManager
        modelBuilder.Entity<Job>()
            .HasOne(j => j.ProjectManager)
            .WithMany(pm => pm.Jobs)
            .HasForeignKey(j => j.ProjectManagerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Job → UserProfile
        modelBuilder.Entity<Job>()
            .HasOne(j => j.UserProfile)
            .WithMany(u => u.Jobs)
            .HasForeignKey(j => j.UserProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        // JobHardware (junction)
        modelBuilder.Entity<JobHardware>()
            .HasOne(jh => jh.Job)
            .WithMany(j => j.JobHardwareLinks)
            .HasForeignKey(jh => jh.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<JobHardware>()
            .HasOne(jh => jh.HardwareItem)
            .WithMany(h => h.JobHardwareLinks)
            .HasForeignKey(jh => jh.HardwareItemId)
            .OnDelete(DeleteBehavior.Restrict);

        // JobTemplateSnapshot → Job
        modelBuilder.Entity<JobTemplateSnapshot>()
            .HasOne(s => s.Job)
            .WithMany(j => j.Snapshots)
            .HasForeignKey(s => s.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // JobTemplateSnapshot → IndividualTemplate
        modelBuilder.Entity<JobTemplateSnapshot>()
            .HasOne(s => s.IndividualTemplate)
            .WithMany(t => t.Snapshots)
            .HasForeignKey(s => s.IndividualTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
