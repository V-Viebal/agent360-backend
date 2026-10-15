using Agent360.API.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Agent360.API.Configurations.OpenApi;

internal class SecuritySchemeTransformer(IAuthenticationSchemeProvider authenticationSchemeProvider, IOptionsMonitor<LogtoOptions> logtoOpts, IWebHostEnvironment webHostEnvironment) : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemes = await authenticationSchemeProvider.GetAllSchemesAsync();

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        var hasBearer = schemes.Any(s => s.Name == JwtBearerDefaults.AuthenticationScheme);

        if (hasBearer)
        {
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                BearerFormat = "JWT",
                Description = "Enter JWT Bearer token authentication",
            };
        }

        var scopesDict = logtoOpts.CurrentValue.Scopes?.ToDictionary(scope => scope, scope => string.Empty) ?? new Dictionary<string, string>();
        var authority = logtoOpts.CurrentValue.Authority.TrimEnd('/');
        var hasOAuthAuthority = Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            && (authorityUri.Scheme == Uri.UriSchemeHttps || authorityUri.Scheme == Uri.UriSchemeHttp);

        if (webHostEnvironment.IsDevelopment() && hasOAuthAuthority)
        {
            document.Components.SecuritySchemes["OAuth2"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        // Use the normalized string rather than Uri.ToString().
                        // Uri.ToString() adds a trailing slash for an authority-only
                        // URI, which would produce `//oidc/auth`.
                        AuthorizationUrl = new Uri($"{authority}/oidc/auth"),
                        TokenUrl = new Uri($"{authority}/oidc/token"),
                        Scopes = scopesDict
                    }
                },
                Description = "OAuth2 Authorization Code Flow with PKCE. Use this to authenticate and obtain an access token.",
            };
        }

        // Apply it as a requirement for all operations
        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
        {
            operation.Value.Security ??= [];

            if (hasBearer)
            {
                operation.Value.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, document)] = []
                });
            }

            if (webHostEnvironment.IsDevelopment() && hasOAuthAuthority)
            {
                operation.Value.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("OAuth2", document)] = [.. scopesDict.Keys]
                });
            }
        }
    }
}