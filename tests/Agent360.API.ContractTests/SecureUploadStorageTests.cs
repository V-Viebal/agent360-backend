using System.Text;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task ConsoleUploadEndpoints_RootAndAgentGroup_ForwardToLiveQwenPawAndReturnUnifiedSchema()
    {
        var b = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Production" });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Configuration["QwenPaw:BaseUrl"] = "http://100.76.185.111:33148";
        b.Configuration["QwenPaw:Instances:core:InternalUrl"] = "http://100.76.185.111:33148";
        b.Configuration["QwenPaw:Instances:core:PublicUrl"] = "https://ai.hmz.one";
        b.Services.AddRouting();
        b.Services.AddApiVersioning(o => o.DefaultApiVersion = new Asp.Versioning.ApiVersion(1));
        b.Services.AddMemoryCache();
        b.Services.AddOptions<Agent360.API.Options.QwenPawOptions>().BindConfiguration(Agent360.API.Options.QwenPawOptions.Section);
        b.Services.AddOptions<StorageOptions>();
        b.Services.AddSingleton<Agent360.API.Features.Agents.Services.FleetRoutingState>();
        b.Services.AddHttpClient<Agent360.API.Features.Agents.Services.IFleetInstanceRoutingService, Agent360.API.Features.Agents.Services.FleetInstanceRoutingService>();
        b.Services.AddHttpClient<Agent360.API.Features.Agents.Services.IQwenPawClient, Agent360.API.Features.Agents.Services.QwenPawClient>();
        b.Services.AddSingleton<Agent360.API.Features.Agents.Services.SessionQueueManager>();
        b.Services.AddSingleton<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
        b.Services.AddSingleton<Agent360.API.Features.Agents.Endpoints.AgentLoopModeStore>();
        b.Services.AddScoped<SecureUploadStorageService>();

        await using var app = b.Build();
        var api = app.NewVersionedApi().MapGroup("api/v{version:apiVersion}").HasApiVersion(1);
        new Agent360.API.Features.Agents.Endpoints.AgentFeaturesEndpoints().MapEndpoint(api);
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
            using var http = new HttpClient { BaseAddress = baseUri };

            foreach (var route in new[] { "api/v1/console/upload", "api/v1/agents/console/upload", "api/v1/agents/mkai/console/upload" })
            {
                using var form = new MultipartFormDataContent();
                var fileBytes = Encoding.UTF8.GetBytes($"# Live Upload Test ({route})\nHello QwenPaw mkai workspace.");
                var byteContent = new ByteArrayContent(fileBytes);
                byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/markdown");
                form.Add(byteContent, "file", "live_chat_upload.md");

                using var req = new HttpRequestMessage(HttpMethod.Post, route) { Content = form };
                req.Headers.Add("X-Agent-Id", "mkai");

                using var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

                var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                Assert.Equal("live_chat_upload.md", root.GetProperty("file_name").GetString());
                Assert.Equal("live_chat_upload.md", root.GetProperty("filename").GetString());
                Assert.Equal(fileBytes.Length, root.GetProperty("size").GetInt64());
                Assert.Equal(fileBytes.Length, root.GetProperty("size_bytes").GetInt64());
                var url = root.GetProperty("url").GetString();
                Assert.False(string.IsNullOrWhiteSpace(url));
                if (url!.StartsWith("/app/working/workspaces/mkai/media/", StringComparison.Ordinal))
                {
                    var previewUrl = root.GetProperty("preview_url").GetString();
                    Assert.False(string.IsNullOrWhiteSpace(previewUrl));
                    Assert.StartsWith("/api/agents/mkai/console/files/", previewUrl);

                    var v1PreviewRoute = previewUrl!.Replace("/api/agents/", "api/v1/agents/");
                    using var previewResp = await http.GetAsync(v1PreviewRoute, TestContext.Current.CancellationToken);
                    Assert.Equal(System.Net.HttpStatusCode.OK, previewResp.StatusCode);
                    var downloadedBytes = await previewResp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(fileBytes, downloadedBytes);
                }
                else
                {
                    Assert.StartsWith("virtual://workspace/uploads/", url);
                }
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
