using System.Net;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="FileAcquirer"/>.</summary>
public class FileAcquirerTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>Creates a temporary directory for test files.</summary>
    public FileAcquirerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbAcquirerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- Helpers ----------

    /// <summary>Creates a dummy PDF-like file with the given content bytes.</summary>
    private string CreateDummyFile(string name, string content = "dummy-pdf-content")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static IndividualTemplate MakeTemplate(
        string templateNumber,
        string? localLink = null,
        string? onlineLink = null,
        string manufacturerName = "Acme")
    {
        return new IndividualTemplate
        {
            Id = 1,
            TemplateNumber = templateNumber,
            LocalLink = localLink,
            OnlineLink = onlineLink,
            PagesToPrint = "1",
            Manufacturer = new Manufacturer { Id = 1, ManufacturerName = manufacturerName }
        };
    }

    private FileAcquirer MakeAcquirer(HttpMessageHandler? handler = null)
    {
        var httpClient = handler != null
            ? new HttpClient(handler)
            : new HttpClient();
        return new FileAcquirer(httpClient);
    }

    // ---------- Tests ----------

    [Fact]
    public async Task AcquireAsync_WithValidLocalLink_CopiesFileToJobSubfolder()
    {
        var sourceFile = CreateDummyFile("source_template.pdf");
        var jobSubfolder = Path.Combine(_tempDir, "job1");
        var template = MakeTemplate("T-001", localLink: sourceFile);

        var acquirer = MakeAcquirer();
        var result = await acquirer.AcquireAsync(template, jobSubfolder);

        Assert.True(File.Exists(result));
        Assert.Equal("Acme_T-001.pdf", Path.GetFileName(result));
        Assert.Equal(Path.Combine(jobSubfolder, "Acme_T-001.pdf"), result);
    }

    [Fact]
    public async Task AcquireAsync_LocalLinkMissing_FallsBackToOnlineLink()
    {
        var jobSubfolder = Path.Combine(_tempDir, "job2");
        const string fakeContent = "downloaded-pdf-bytes";

        var handler = new FakeHttpHandler(fakeContent);
        var template = MakeTemplate(
            "T-002",
            localLink: Path.Combine(_tempDir, "does_not_exist.pdf"),
            onlineLink: "http://example.com/template.pdf");

        var acquirer = MakeAcquirer(handler);
        var result = await acquirer.AcquireAsync(template, jobSubfolder);

        Assert.True(File.Exists(result));
        Assert.Equal(fakeContent, File.ReadAllText(result));
    }

    [Fact]
    public async Task AcquireAsync_NoLocalOrOnlineLink_ThrowsInvalidOperationException()
    {
        var jobSubfolder = Path.Combine(_tempDir, "job3");
        var template = MakeTemplate("T-003");

        var acquirer = MakeAcquirer();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            acquirer.AcquireAsync(template, jobSubfolder));
    }

    [Fact]
    public async Task AcquireAsync_CreatesJobSubfolderIfMissing()
    {
        var sourceFile = CreateDummyFile("source_for_mkdir.pdf");
        var jobSubfolder = Path.Combine(_tempDir, "new_job_folder");
        var template = MakeTemplate("T-004", localLink: sourceFile);

        Assert.False(Directory.Exists(jobSubfolder));

        var acquirer = MakeAcquirer();
        await acquirer.AcquireAsync(template, jobSubfolder);

        Assert.True(Directory.Exists(jobSubfolder));
    }

    [Fact]
    public async Task AcquireAsync_FileNameSanitizesInvalidChars()
    {
        var sourceFile = CreateDummyFile("source_sanitize.pdf");
        var jobSubfolder = Path.Combine(_tempDir, "job_sanitize");
        var template = MakeTemplate("T/005", localLink: sourceFile, manufacturerName: "Acme:Corp");

        var acquirer = MakeAcquirer();
        var result = await acquirer.AcquireAsync(template, jobSubfolder);

        // Invalid path chars should be replaced with underscores.
        Assert.True(File.Exists(result));
        Assert.DoesNotContain("/", Path.GetFileName(result));
        Assert.DoesNotContain(":", Path.GetFileName(result));
    }

    // ---------- Fake HTTP handler ----------

    /// <summary>
    /// A minimal <see cref="HttpMessageHandler"/> that returns a fixed string body
    /// with a 200 OK status code for any request.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _content;

        /// <summary>Initializes the handler with the given response body.</summary>
        public FakeHttpHandler(string content) => _content = content;

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_content)
            };
            return Task.FromResult(response);
        }
    }
}
