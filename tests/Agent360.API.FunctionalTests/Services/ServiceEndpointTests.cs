using System.Net;

namespace Agent360.API.FunctionalTests.Services;

public sealed class ServiceEndpointTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Theory]
    [InlineData("/alive")]
    [InlineData("/health")]
    public async Task HealthEndpoints_AreAnonymousAndHealthy(string path)
    {
        // Arrange
        // Theory data supplies the anonymous service endpoint path.

        // Action
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}