using System.Text;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class SecureUploadStorageTests
{
    private sealed class SimpleOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private static SecureUploadStorageService CreateService()
    {
        var optionsMonitor = new SimpleOptionsMonitor<StorageOptions>(new StorageOptions
        {
            Provider = "Local"
        });

        return new SecureUploadStorageService(
            optionsMonitor,
            new SimpleHttpClientFactory(),
            NullLogger<SecureUploadStorageService>.Instance);
    }

    [Fact]
    public async Task StoreFileAsync_MasksPhysicalPath_WithVirtualUri()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "agent360-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = "console.log('test upload');";
            var bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);

            var formFile = new FormFile(stream, 0, bytes.Length, "file", "my_script.js")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/javascript"
            };

            var service = CreateService();
            var (success, error, response) = await service.StoreFileAsync(
                formFile,
                tempRoot,
                CancellationToken.None);

            Assert.True(success);
            Assert.Null(error);
            Assert.NotNull(response);
            Assert.StartsWith("virtual://workspace/uploads/", response.VirtualUri);
            Assert.DoesNotContain(tempRoot, response.VirtualUri); // Never leak server paths
            Assert.Equal(response.VirtualUri, response.Url);
            Assert.Equal("my_script.js", response.FileName);
            Assert.Equal(bytes.Length, response.Size);
            Assert.Equal("application/javascript", response.MimeType);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StoreFileAsync_RejectsDangerousExtensions()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "agent360-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = Encoding.UTF8.GetBytes("fake exe");
            using var stream = new MemoryStream(bytes);

            var formFile = new FormFile(stream, 0, bytes.Length, "file", "malicious.exe")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/x-msdownload"
            };

            var service = CreateService();
            var (success, error, response) = await service.StoreFileAsync(
                formFile,
                tempRoot,
                CancellationToken.None);

            Assert.False(success);
            Assert.NotNull(error);
            Assert.Contains("not permitted", error);
            Assert.Null(response);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
