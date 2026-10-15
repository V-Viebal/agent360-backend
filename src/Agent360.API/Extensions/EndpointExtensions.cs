using Agent360.API.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Agent360.API.Extensions;

/// <summary>
/// Provides extension methods for registering and mapping endpoint implementations that conform to the IEndpoint
/// interface within a dependency injection container and the application's routing pipeline.
/// </summary>
/// <remarks>Use these extensions to streamline the discovery, registration, and mapping of endpoint types in
/// modular applications. These methods support automatic registration of endpoints from assemblies and ensure that all
/// registered endpoints are mapped to the application's routing system during startup.</remarks>
public static class EndpointExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers all non-abstract, non-interface types implementing the IEndpoint interface from the specified
        /// assembly as transient services.
        /// </summary>
        /// <remarks>Use this method to automatically discover and register endpoint implementations
        /// without manually specifying each type. This is useful for modular applications where endpoints are defined
        /// across multiple assemblies.</remarks>
        /// <param name="assembly">The assembly to scan for types that implement the IEndpoint interface. Only concrete, non-abstract types are
        /// registered.</param>
        /// <returns>The IServiceCollection instance with the discovered endpoint services registered.</returns>
        public IServiceCollection AddEndpoints(Assembly assembly)
        {
            var endpointDescriptors = assembly.GetExportedTypes()
                .Where(t => t is { IsInterface: false, IsAbstract: false } && typeof(IEndpoint).IsAssignableFrom(t))
                .Select(t => ServiceDescriptor.Transient(typeof(IEndpoint), t))
                .ToArray();

            services.TryAddEnumerable(endpointDescriptors);

            return services;
        }
    }

    extension(WebApplication webApp)
    {
        /// <summary>
        /// Maps all registered endpoints to the application's routing pipeline, optionally within a specified route
        /// group.
        /// </summary>
        /// <remarks>Use this method to ensure that all endpoints implementing IEndpoint are registered
        /// with the application's routing system. This is typically called during application startup after all
        /// endpoints have been configured.</remarks>
        /// <param name="routeGroupBuilder">An optional route group builder that defines the route group to which endpoints will be mapped. If null,
        /// endpoints are mapped directly to the application's root route builder.</param>
        /// <returns>The current web application instance with endpoints mapped.</returns>
        public WebApplication MapEndpoints(RouteGroupBuilder? routeGroupBuilder = null)
        {
            IEnumerable<IEndpoint> endpoints = webApp.Services.GetRequiredService<IEnumerable<IEndpoint>>();
            IEndpointRouteBuilder routeBuilder = routeGroupBuilder is null ? webApp : routeGroupBuilder;

            foreach (var endpoint in endpoints)
            {
                endpoint.MapEndpoint(routeBuilder);
            }

            return webApp;
        }
    }
}