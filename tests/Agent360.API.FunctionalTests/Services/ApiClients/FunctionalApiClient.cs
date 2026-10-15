using Hmz.Core.Testing.Http;

namespace Agent360.API.FunctionalTests.Services.ApiClients;

public sealed partial class FunctionalApiClient(HttpClient client)
    : FunctionalApiClientBase(client);