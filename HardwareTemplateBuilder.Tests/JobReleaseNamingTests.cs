using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="JobReleaseNaming"/> and <see cref="JobPackageGenerationService.ActiveReleaseId"/>.</summary>
public class JobReleaseNamingTests
{
    [Theory]
    [InlineData("24117", null, "24117")]
    [InlineData("24117", "", "24117")]
    [InlineData("24117", "  ", "24117")]
    [InlineData("24117", "Addendum 1", "24117 Addendum 1")]
    [InlineData("24117", "  Rev A ", "24117 Rev A")]
    public void CoverJobNumber_AppendsReleaseOnlyWhenThereIsOne(string jobNumber, string? release, string expected) =>
        Assert.Equal(expected, JobReleaseNaming.CoverJobNumber(jobNumber, release));

    [Fact]
    public void PackageFileName_IsPerRelease_AndSafeForWindows()
    {
        Assert.Equal("Templates for job 24117.pdf", JobReleaseNaming.PackageFileName("24117"));
        Assert.Equal("Templates for job 24117 Addendum 1.pdf", JobReleaseNaming.PackageFileName("24117 Addendum 1"));
        Assert.Equal("Templates for job 24117 Rev-A-B.pdf", JobReleaseNaming.PackageFileName("24117 Rev/A:B"));
    }

    [Fact]
    public void IsPackageFile_MatchesAnyReleasesPackageOnly()
    {
        Assert.True(JobReleaseNaming.IsPackageFile("Templates for job 24117.pdf"));
        Assert.True(JobReleaseNaming.IsPackageFile("Templates for job 24117 Addendum 1.pdf"));
        Assert.False(JobReleaseNaming.IsPackageFile("Cover Sheet - Acme.pdf"));
        Assert.False(JobReleaseNaming.IsPackageFile("job_history.txt"));
    }

    [Fact]
    public void ActiveReleaseId_IsTheLastPickedRelease_OrNullForBaseOrMissing()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HtbReleaseTests_{Guid.NewGuid():N}").Options;
        using var ctx = new AppDbContext(options);

        var baseJob = new Job { JobNumber = "1", JobName = "Base" };
        var releaseJob = new Job { JobNumber = "2", JobName = "Release" };
        var staleJob = new Job { JobNumber = "3", JobName = "Stale" };
        ctx.Jobs.AddRange(baseJob, releaseJob, staleJob);
        ctx.SaveChanges();

        var release = new JobRelease { JobId = releaseJob.Id, ReleaseNumber = 1, ReleaseLabel = "Addendum 1" };
        ctx.JobReleases.Add(release);
        ctx.SaveChanges();
        releaseJob.LastActiveReleaseId = release.Id;
        staleJob.LastActiveReleaseId = 9999;
        ctx.SaveChanges();

        Assert.Null(JobPackageGenerationService.ActiveReleaseId(ctx, baseJob.Id));
        Assert.Equal(release.Id, JobPackageGenerationService.ActiveReleaseId(ctx, releaseJob.Id));
        Assert.Null(JobPackageGenerationService.ActiveReleaseId(ctx, staleJob.Id));
    }
}
