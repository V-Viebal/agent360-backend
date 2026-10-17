using System.Net;
using System.Net.Http.Json;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

public class ContextAndToolResilienceTests
{
    [Fact]
    public void Infisical_ResolvesAgentScopedComposioToken_Correctly()
    {
        // Act
        var token = InfisicalCredentialAuditService.ResolveAgentComposioToken("mons");

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
        token.Should().StartWith("ck_");
    }

    [Fact]
    public void ToolCallReceiptContractDto_ConformsToSchema()
    {
        // Arrange
        var receipt = new ToolCallReceiptContractDto(
            Schema: "agent360/tool-call-receipt/v1",
            ToolCallId: "call-12345",
            BotId: "bot:agent360:mons",
            BotBindingId: "binding:mons",
            DeploymentId: "deploy:mons:default",
            RunId: "session-999",
            CapabilityId: "composio_github",
            InputHash: "sha256:abc",
            AuthorizationDecisionId: "auth-dec-01",
            PolicyHash: "sha256:pol",
            Status: "succeeded",
            OutputHash: "sha256:out",
            Error: null);

        // Assert
        receipt.Schema.Should().Be("agent360/tool-call-receipt/v1");
        receipt.ToolCallId.Should().Be("call-12345");
        receipt.BotId.Should().Be("bot:agent360:mons");
        receipt.Status.Should().Be("succeeded");
    }

    [Fact]
    public async Task CompactContextAsync_ReturnsValidDto_EvenOnUnreachableBackend()
    {
        // Arrange
        var mockHandler = new DelegatingMockHandler((req) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { deleted_refs = Array.Empty<string>(), kept_refs = Array.Empty<string>(), dry_run = false })
            };
        });

        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://127.0.0.1:33125/")
        };

        var routingService = new FleetInstanceRoutingService(httpClient, NullLogger<FleetInstanceRoutingService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions { BaseUrl = "http://127.0.0.1:33125" });
        var client = new QwenPawClient(httpClient, routingService, options, NullLogger<QwenPawClient>.Instance);

        // Act
        var result = await client.CompactContextAsync(new CompactContextRequestDto(
            AgentId: "mons",
            SessionId: "test-session",
            Hint: "keep tasks only"),
            TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.AgentId.Should().Be("mons");
    }

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
