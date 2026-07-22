using System.Net;
using System.Net.Http;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="TemplateRefreshService"/>.</summary>
public class TemplateRefreshServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppDbContext _context;

    /// <summary>Creates a fresh in-memory SQLite database and a temporary directory for downloaded files.</summary>
    public TemplateRefreshServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbRefreshTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
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
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- Helpers ----------

    /// <summary>Returns the minimum valid PDF header bytes (%PDF-).</summary>
    private static byte[] PdfBytes() => new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

    /// <summary>Creates a handler that returns the same <paramref name="body"/> for every request.</summary>
    private static HttpMessageHandler ConstantHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        => new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) }));

    /// <summary>Creates a handler driven by a delegate.</summary>
    private static HttpMessageHandler DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> fn)
        => new StubHandler(fn);

    private TemplateRefreshService MakeService(HttpMessageHandler handler)
        => new(_context, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    /// <summary>
    /// Seeds a minimal <see cref="IndividualTemplate"/> with the given online link and returns its ID.
    /// Reuses or creates the manufacturer and a shared description record as needed.
    /// DoorMaterialId=1 (Metal) is always available via <c>HasData</c> seeding.
    /// </summary>
    private int SeedTemplate(string number, string onlineLink, string manufacturerName = "Acme")
    {
        var mfr = _context.Manufacturers.FirstOrDefault(m => m.ManufacturerName == manufacturerName);
        if (mfr == null)
        {
            mfr = _context.Manufacturers.Add(new Manufacturer { ManufacturerName = manufacturerName }).Entity;
            _context.SaveChanges();
        }

        var desc = _context.Descriptions.FirstOrDefault();
        if (desc == null)
        {
            desc = _context.Descriptions.Add(new Description { DescriptionText = "Hardware" }).Entity;
            _context.SaveChanges();
        }

        var template = new IndividualTemplate
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            DoorMaterialId = 1, // "Metal" is always seeded by AppDbContext.HasData
            TemplateNumber = number,
            OnlineLink     = onlineLink,
            PagesToPrint   = "1",
        };
        _context.IndividualTemplates.Add(template);
        _context.SaveChanges();
        return template.Id;
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_NoTemplatesWithOnlineLink_ReturnsZeroAttempted()
    {
        var result = await MakeService(ConstantHandler(PdfBytes())).RefreshAsync(_tempDir);

        Assert.Equal(0, result.TotalAttempted);
        Assert.Equal(0, result.SuccessCount);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task RefreshAsync_AllDownloadsSucceed_ReturnsFullSuccessCount()
    {
        SeedTemplate("T-001", "http://example.com/t001.pdf");
        SeedTemplate("T-002", "http://example.com/t002.pdf");

        var result = await MakeService(ConstantHandler(PdfBytes())).RefreshAsync(_tempDir);

        Assert.Equal(2, result.TotalAttempted);
        Assert.Equal(2, result.SuccessCount);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task RefreshAsync_AllDownloadsSucceed_UpdatesLocalLinksInDatabase()
    {
        var id1 = SeedTemplate("T-001", "http://example.com/t001.pdf");
        var id2 = SeedTemplate("T-002", "http://example.com/t002.pdf");

        await MakeService(ConstantHandler(PdfBytes())).RefreshAsync(_tempDir);

        _context.ChangeTracker.Clear();
        var t1 = _context.IndividualTemplates.Find(id1)!;
        var t2 = _context.IndividualTemplates.Find(id2)!;

        Assert.False(string.IsNullOrEmpty(t1.LocalLink), "T-001 LocalLink should be set");
        Assert.False(string.IsNullOrEmpty(t2.LocalLink), "T-002 LocalLink should be set");
        Assert.True(File.Exists(t1.LocalLink));
        Assert.True(File.Exists(t2.LocalLink));
    }

    [Fact]
    public async Task RefreshAsync_OneDownloadFails_RecordsFailureAndContinues()
    {
        SeedTemplate("T-001", "http://example.com/fail.pdf");
        SeedTemplate("T-002", "http://example.com/ok.pdf");

        var handler = DelegateHandler(req =>
        {
            // Return non-PDF bytes for the URL containing "fail", PDF bytes otherwise.
            var bytes = req.RequestUri!.AbsolutePath.Contains("fail")
                ? new byte[] { 0x00, 0x00 }
                : PdfBytes();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes) });
        });

        var result = await MakeService(handler).RefreshAsync(_tempDir);

        Assert.Equal(2, result.TotalAttempted);
        Assert.Equal(1, result.SuccessCount);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task RefreshAsync_MultipleFailures_DoNotPreventSubsequentSuccesses()
    {
        SeedTemplate("T-001", "http://example.com/fail1.pdf");
        SeedTemplate("T-002", "http://example.com/fail2.pdf");
        SeedTemplate("T-003", "http://example.com/ok.pdf");

        var handler = DelegateHandler(req =>
        {
            var bytes = req.RequestUri!.AbsolutePath.Contains("fail")
                ? new byte[] { 0x00 }
                : PdfBytes();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes) });
        });

        var result = await MakeService(handler).RefreshAsync(_tempDir);

        Assert.Equal(3, result.TotalAttempted);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(2, result.Failures.Count);
    }

    [Fact]
    public async Task RefreshAsync_NonPdfResponse_RecordsFailureWithDownloadMessage()
    {
        SeedTemplate("T-001", "http://example.com/t001.pdf");

        var htmlBytes = System.Text.Encoding.UTF8.GetBytes("<html>Error page</html>");
        var result = await MakeService(ConstantHandler(htmlBytes)).RefreshAsync(_tempDir);

        Assert.Equal(1, result.TotalAttempted);
        Assert.Equal(0, result.SuccessCount);
        Assert.Single(result.Failures);
        Assert.Contains("Download failed", result.Failures[0].ErrorMessage);
    }

    [Fact]
    public async Task RefreshAsync_Failure_IncludesTemplateNameAndId()
    {
        SeedTemplate("T-XYZ", "http://example.com/t.pdf", "Schlage");

        var htmlBytes = System.Text.Encoding.UTF8.GetBytes("<html>err</html>");
        var result = await MakeService(ConstantHandler(htmlBytes)).RefreshAsync(_tempDir);

        var failure = Assert.Single(result.Failures);
        Assert.Contains("Schlage", failure.TemplateName);
        Assert.Contains("T-XYZ", failure.TemplateName);
        Assert.True(failure.TemplateId > 0);
    }

    [Fact]
    public async Task RefreshAsync_Cancelled_ThrowsOperationCanceledException()
    {
        SeedTemplate("T-001", "http://example.com/t001.pdf");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MakeService(ConstantHandler(PdfBytes())).RefreshAsync(_tempDir, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RefreshAsync_TemplateWithoutOnlineLink_IsNotAttempted()
    {
        var mfr  = _context.Manufacturers.Add(new Manufacturer { ManufacturerName = "Acme" }).Entity;
        var desc = _context.Descriptions.Add(new Description { DescriptionText = "Hardware" }).Entity;
        _context.IndividualTemplates.Add(new IndividualTemplate
        {
            Manufacturer   = mfr,
            Description    = desc,
            DoorMaterialId = 1,
            TemplateNumber = "NOLINK",
            OnlineLink     = null,
            PagesToPrint   = "1",
        });
        _context.SaveChanges();

        SeedTemplate("T-001", "http://example.com/t001.pdf", "Acme");

        var result = await MakeService(ConstantHandler(PdfBytes())).RefreshAsync(_tempDir);

        Assert.Equal(1, result.TotalAttempted);
        Assert.Equal(1, result.SuccessCount);
    }

    [Fact]
    public async Task RefreshAsync_ReportsProgressPerTemplate()
    {
        SeedTemplate("T-001", "http://example.com/t001.pdf");
        SeedTemplate("T-002", "http://example.com/t002.pdf");
        SeedTemplate("T-003", "http://example.com/t003.pdf");

        var reports = new List<RefreshProgress>();
        IProgress<RefreshProgress> progress = new SyncProgress<RefreshProgress>(p => reports.Add(p));

        await MakeService(ConstantHandler(PdfBytes())).RefreshAsync(_tempDir, progress);

        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.Equal(3, r.Total));
        Assert.Equal(1, reports[0].Current);
        Assert.Equal(2, reports[1].Current);
        Assert.Equal(3, reports[2].Current);
        Assert.All(reports, r => Assert.False(string.IsNullOrEmpty(r.TemplateName)));
    }

    // ---------- RefreshSingleAsync ----------

    [Fact]
    public async Task RefreshSingleAsync_TemplateNotFound_ReturnsFalse()
    {
        var result = await MakeService(ConstantHandler(PdfBytes())).RefreshSingleAsync(9999, _tempDir);
        Assert.False(result);
    }

    [Fact]
    public async Task RefreshSingleAsync_NoOnlineLink_ReturnsFalse()
    {
        var mfr  = _context.Manufacturers.Add(new Manufacturer { ManufacturerName = "Acme" }).Entity;
        var desc = _context.Descriptions.Add(new Description { DescriptionText = "Hardware" }).Entity;
        var t = _context.IndividualTemplates.Add(new IndividualTemplate
        {
            Manufacturer   = mfr,
            Description    = desc,
            DoorMaterialId = 1,
            TemplateNumber = "T-NOLINK",
            OnlineLink     = null,
            PagesToPrint   = "1",
        }).Entity;
        _context.SaveChanges();

        var result = await MakeService(ConstantHandler(PdfBytes())).RefreshSingleAsync(t.Id, _tempDir);
        Assert.False(result);
    }

    [Fact]
    public async Task RefreshSingleAsync_DownloadSucceeds_ReturnsTrueAndPersistsLocalLink()
    {
        var id = SeedTemplate("T-001", "http://example.com/t001.pdf");

        var result = await MakeService(ConstantHandler(PdfBytes())).RefreshSingleAsync(id, _tempDir);

        Assert.True(result);
        _context.ChangeTracker.Clear();
        var saved = _context.IndividualTemplates.Find(id)!;
        Assert.False(string.IsNullOrEmpty(saved.LocalLink));
        Assert.True(File.Exists(saved.LocalLink));
    }

    [Fact]
    public async Task RefreshSingleAsync_NonPdfResponse_ThrowsInvalidOperationException()
    {
        var id = SeedTemplate("T-001", "http://example.com/t001.pdf");

        var htmlBytes = System.Text.Encoding.UTF8.GetBytes("<html>Not a PDF</html>");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeService(ConstantHandler(htmlBytes)).RefreshSingleAsync(id, _tempDir));
    }
}

/// <summary>
/// A synchronous <see cref="IProgress{T}"/> implementation that invokes the callback inline,
/// giving tests deterministic ordering without needing <c>Task.Delay</c>.
/// </summary>
file sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;
    public SyncProgress(Action<T> callback) => _callback = callback;
    public void Report(T value) => _callback(value);
}

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> that delegates every request to the provided function.
/// </summary>
file sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken _)
        => _respond(request);
}
