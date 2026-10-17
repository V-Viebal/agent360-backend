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

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
