namespace Agent360.API.FunctionalTests;

public abstract class FunctionalTestBase(FunctionalTestFixture fixture)
    : Hmz.Core.Testing.Fixtures.FunctionalTestBase<
        FunctionalTestFixture,
        ApiWebApplicationFactory,
        Services.ApiClients.FunctionalApiClient>(fixture)
{
    protected HttpClient _client => Client;
}