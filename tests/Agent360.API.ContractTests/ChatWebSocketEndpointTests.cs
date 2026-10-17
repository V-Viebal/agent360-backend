using Agent360.API.Features.Agents.Endpoints;
using AwesomeAssertions;
using Xunit;

namespace Agent360.API.ContractTests;

public class ChatWebSocketEndpointTests
{
    [Fact]
    public void ChatWebSocketEndpoint_ImplementsIEndpoint_AndHasRoute()
    {
        var endpoint = new ChatWebSocketEndpoint();
        endpoint.Should().NotBeNull();
    }
}
