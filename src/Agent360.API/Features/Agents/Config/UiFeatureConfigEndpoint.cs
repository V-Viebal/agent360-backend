using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;

namespace Agent360.API.Features.Agents.Config;

public sealed record UiFeatureCapabilities(
    bool CanForkTurn,
    bool CloudStoreEnabled,
    bool PawAppsEnabled,
    bool DesktopWidgetBrowserCdpEnabled,
    bool ComposioHubEnabled,
    bool MediaUploadsEnabled,
    bool AdvancedApprovalsEnabled);

public sealed class UiFeatureConfigEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "config/features",
            () =>
            {
                var features = new UiFeatureCapabilities(
                    CanForkTurn: false, // Hidden as per Rome/QwenPaw contract
                    CloudStoreEnabled: false, // Rome cloud store dropped
                    PawAppsEnabled: true,
                    DesktopWidgetBrowserCdpEnabled: true,
                    ComposioHubEnabled: true,
                    MediaUploadsEnabled: true,
                    AdvancedApprovalsEnabled: false);

                return TypedResults.Ok(features);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetUiFeatureFlags")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Retrieve UI feature flags to prune unsupported controls like Fork/Branch")
            .AllowAnonymous();
    }
}
