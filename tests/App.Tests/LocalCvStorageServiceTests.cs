using App.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

public class LocalCvStorageServiceTests : IDisposable
{
    private readonly string _testStorageDir;
    private readonly LocalCvStorageService _service;

    public LocalCvStorageServiceTests()
    {
        _testStorageDir = Path.Combine(Path.GetTempPath(), "app_svc_test_" + Guid.NewGuid());
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["APP_CV_STORAGE_PATH"] = _testStorageDir
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        _service = new LocalCvStorageService(config, NullLogger<LocalCvStorageService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testStorageDir))
        {
            try { Directory.Delete(_testStorageDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SaveCvAsync_ValidPdf_SavesSuccessfullyAndReturnsUrl()
    {
        var content = "Dummy PDF binary content"u8.ToArray();
        using var stream = new MemoryStream(content);

        var url = await _service.SaveCvAsync(stream, "my_resume.pdf", "application/pdf");

        Assert.StartsWith("/api/applications/cv/", url);

        var fileName = url.Replace("/api/applications/cv/", "");
        var savedFilePath = Path.Combine(_testStorageDir, fileName);
        Assert.True(File.Exists(savedFilePath));

        // Read it back
        var readResult = await _service.GetCvAsync(fileName);
        Assert.NotNull(readResult);
        Assert.Equal("application/pdf", readResult.Value.ContentType);

        using var readMs = new MemoryStream();
        await readResult.Value.Stream.CopyToAsync(readMs);
        readResult.Value.Stream.Dispose();
        Assert.Equal(content, readMs.ToArray());
    }

    [Fact]
    public async Task SaveCvAsync_InvalidExtension_ThrowsArgumentException()
    {
        var content = "Malicious executable"u8.ToArray();
        using var stream = new MemoryStream(content);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SaveCvAsync(stream, "virus.exe", "application/x-msdownload"));
    }

    [Fact]
    public async Task SaveCvAsync_OversizedFile_ThrowsArgumentException()
    {
        // 6MB > 5MB limit
        var oversizedStream = new MemoryStream(new byte[6 * 1024 * 1024]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SaveCvAsync(oversizedStream, "huge.pdf", "application/pdf"));
    }

    [Fact]
    public async Task DeleteCvAsync_ExistingFile_DeletesAndReturnsTrue()
    {
        var content = "Some CV"u8.ToArray();
        using var stream = new MemoryStream(content);
        var url = await _service.SaveCvAsync(stream, "test.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        var fileName = url.Replace("/api/applications/cv/", "");
        var deleted = await _service.DeleteCvAsync(fileName);

        Assert.True(deleted);
        Assert.False(File.Exists(Path.Combine(_testStorageDir, fileName)));
    }
}
