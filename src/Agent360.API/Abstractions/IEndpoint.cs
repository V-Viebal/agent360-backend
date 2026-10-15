namespace Agent360.API.Abstractions;

/// <summary>
/// Defines a contract for mapping an endpoint to an endpoint route builder.
/// </summary>
/// <remarks>Implement this interface to configure and register endpoints with the application's routing system.
/// This is typically used in web applications to modularize endpoint registration logic.</remarks>
public interface IEndpoint
{
    /// <summary>
    /// Configures one or more endpoints on the specified endpoint route builder.
    /// </summary>
    /// <remarks>This method is typically called during application startup to define routing for HTTP
    /// endpoints. The provided builder is used to register routes, handlers, or middleware components as part of the
    /// application's request pipeline.</remarks>
    /// <param name="routeBuilder">The endpoint route builder to which endpoints will be mapped. Cannot be null.</param>
    void MapEndpoint(IEndpointRouteBuilder routeBuilder);
}