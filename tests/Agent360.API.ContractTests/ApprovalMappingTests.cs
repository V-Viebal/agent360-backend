using System.Net;
using System.Net.Http.Json;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

public class ApprovalMappingTests
{
    [Fact]
    public async Task GetApprovalsAsync_QueriesCorrectNativeEndpoint()
    {
        string? requestedUrl = null;
        var mockHandler = new DelegatingMockHandler((req) =>
        {
            requestedUrl = req.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApprovalListResponseDto(Array.Empty<ApprovalItemDto>(), 0))
            };
        });

        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://127.0.0.1:33125/")
        };

        var routingService = new FleetInstanceRoutingService(httpClient, NullLogger<FleetInstanceRoutingService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions { BaseUrl = "http://127.0.0.1:33125" });
        var client = new QwenPawClient(httpClient, routingService, options, NullLogger<QwenPawClient>.Instance);

        var result = await client.GetApprovalsAsync("session-123", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        requestedUrl.Should().Be("/api/approval/list?session_id=session-123");
    }

    [Fact]
    public async Task GetApprovalsAsync_ConcurrentCallers_ShareOneFanOut()
    {
        var calls = 0;
        var mockHandler = new DelegatingMockHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("login") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { token = "mock-token" })
                };
            }
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApprovalListResponseDto(Array.Empty<ApprovalItemDto>(), 0))
            };
        });
        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://127.0.0.1:33125/") };
        var routingService = new FleetInstanceRoutingService(httpClient, NullLogger<FleetInstanceRoutingService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions { BaseUrl = "http://127.0.0.1:33125" });
        var client = new QwenPawClient(httpClient, routingService, options, NullLogger<QwenPawClient>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var sessionKey = "shared-" + Guid.NewGuid().ToString("N");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetApprovalsAsync(sessionKey, ct)));

        calls.Should().Be(routingService.Instances.Count);
    }

    [Fact]
    public async Task GetApprovalsAsync_CoalescesPolls_AndBacksOffSlowRuntime()
    {
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var mockHandler = new DelegatingMockHandler(req =>
        {
            calls.AddOrUpdate(req.RequestUri!.Host, 1, (_, n) => n + 1);
            if (req.RequestUri.Host == "100.76.185.111")
            {
                throw new TaskCanceledException("core overloaded");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApprovalListResponseDto(Array.Empty<ApprovalItemDto>(), 0))
            };
        });
        var httpClient = new HttpClient(mockHandler);
        var routing = new FleetInstanceRoutingService(httpClient, NullLogger<FleetInstanceRoutingService>.Instance);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var client = new QwenPawClient(httpClient, routing, Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()),
            NullLogger<QwenPawClient>.Instance, cache);
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetApprovalsAsync(null, ct)));
        calls.GetValueOrDefault("100.76.185.111").Should().Be(1, "concurrent identical polls share one fan-out");

        cache.Remove("agent360:approvals:*");
        await client.GetApprovalsAsync(null, ct);
        calls.GetValueOrDefault("100.76.185.111").Should().Be(1, "a runtime that just timed out is backed off");
    }

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
