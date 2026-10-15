namespace Agent360.API.FunctionalTests.Services.ApiClients;

public sealed partial class FunctionalApiClient
{
    public Task<HttpResponseMessage> GetLanguagesAsync(
        bool activeOnly,
        CancellationToken cancellationToken = default) =>
        Client.GetAsync(
            $"/api/v1/reference-data/languages?activeOnly={activeOnly.ToString().ToLowerInvariant()}",
            cancellationToken);

    public Task<HttpResponseMessage> GetCurrenciesAsync(
        bool activeOnly,
        CancellationToken cancellationToken = default) =>
        Client.GetAsync(
            $"/api/v1/reference-data/currencies?activeOnly={activeOnly.ToString().ToLowerInvariant()}",
            cancellationToken);
}