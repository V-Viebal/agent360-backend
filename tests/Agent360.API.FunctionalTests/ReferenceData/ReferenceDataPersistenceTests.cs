using Agent360.ReferenceData.Domain.CurrencyAggregate;
using Agent360.ReferenceData.Domain.LanguageAggregate;
using Agent360.ReferenceData.Infrastructure.Persistence;
using Agent360.ReferenceData.UseCase.Currencies.Common;
using Agent360.ReferenceData.UseCase.Languages.Common;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Agent360.API.FunctionalTests.ReferenceData;

// Former EF InMemory query scenarios now cross real PostgreSQL and the HTTP boundary.
public sealed class ReferenceDataPersistenceTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task Languages_ActiveOnly_FiltersRowsStoredInPostgreSql()
    {
        // Arrange
        await using (var scope = Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Languages.Add(Language.Create("German", "de"));
            var inactive = Language.Create("French", "fr");
            inactive.Update("French", isActive: false);
            db.Languages.Add(inactive);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.External);

        // Action
        using var activeResponse = await Api.GetLanguagesAsync(true, TestContext.Current.CancellationToken);
        using var allResponse = await Api.GetLanguagesAsync(false, TestContext.Current.CancellationToken);

        // Assertion
        activeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        allResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var active = await activeResponse.ReadRequiredJsonAsync<IReadOnlyList<LanguageResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var all = await allResponse.ReadRequiredJsonAsync<IReadOnlyList<LanguageResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        active!.Select(x => x.Code).Should().Contain("de").And.NotContain("fr");
        all!.Select(x => x.Code).Should().Contain(["de", "fr"]);
    }

    [Fact]
    public async Task Currencies_ActiveOnly_FiltersRowsStoredInPostgreSql()
    {
        // Arrange
        await using (var scope = Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Currencies.Add(Currency.Create("EUR", "€", 2));
            var inactive = Currency.Create("GBP", "£", 2);
            inactive.Deactivate();
            db.Currencies.Add(inactive);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.External);

        // Action
        using var activeResponse = await Api.GetCurrenciesAsync(true, TestContext.Current.CancellationToken);
        using var allResponse = await Api.GetCurrenciesAsync(false, TestContext.Current.CancellationToken);

        // Assertion
        activeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        allResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var active = await activeResponse.ReadRequiredJsonAsync<IReadOnlyList<CurrencyResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var all = await allResponse.ReadRequiredJsonAsync<IReadOnlyList<CurrencyResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        active!.Select(x => x.Code).Should().Contain("EUR").And.NotContain("GBP");
        all!.Select(x => x.Code).Should().Contain(["EUR", "GBP"]);
    }
}