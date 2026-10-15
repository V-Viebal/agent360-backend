using Agent360.ReferenceData.UseCase.Currencies.Common;
using Agent360.ReferenceData.UseCase.Languages.Common;
using System.Net;

namespace Agent360.API.FunctionalTests.ReferenceData;

public sealed class ReferenceDataEndpointTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task GetLanguages_WithAuthenticatedUser_ReturnsSeededRows()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.External);

        // Action
        using var response = await Api.GetLanguagesAsync(
            activeOnly: true,
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.ReadRequiredJsonAsync<IReadOnlyList<LanguageResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        payload.Should().NotBeNull();
        payload!.Select(language => language.Code).Should().Contain(["en", "vi"]);
    }

    [Fact]
    public async Task GetCurrencies_WithAuthenticatedUser_ReturnsSeededRows()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.External);

        // Action
        using var response = await Api.GetCurrenciesAsync(
            activeOnly: false,
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.ReadRequiredJsonAsync<IReadOnlyList<CurrencyResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        payload.Should().NotBeNull();
        payload!.Select(currency => currency.Code).Should().Contain(["USD", "VND"]);
    }
}