namespace Agent360.AppHost.Extensions;

internal static class PodmanExtensions
{
    public static IResourceBuilder<ContainerResource> WithPodmanHostFix(
        this IResourceBuilder<ContainerResource> builder)
    {
        // Check whether Aspire is currently running in a Podman container
        var isPodman = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") == "podman";

        if (isPodman)
        {
            // Automatically add the host entry for aspire.dev.internal when running in Podman
            return builder.WithArgs("--add-host", "aspire.dev.internal:host-gateway");
        }

        return builder;
    }
}