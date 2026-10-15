using Agent360.ReferenceData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent360.API.FunctionalTests;

public sealed class DevelopmentSeedingTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task DevelopmentTestHost_StillSeedsCoreData()
    {
        // Arrange
        await using (var scope = Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Languages.Where(language => language.Code == "en")
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await using var developmentHost = Fixture.CreateApplicationFactory(
            Environments.Development);

        // Action
        using var client = developmentHost.CreateClient();
        using var response = await client.GetAsync(
            "/health",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        // Assertion
        await using var verificationScope = Fixture.Factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verificationDb.Languages.AnyAsync(
            language => language.Code == "en",
            TestContext.Current.CancellationToken)).Should().BeTrue();
    }
}