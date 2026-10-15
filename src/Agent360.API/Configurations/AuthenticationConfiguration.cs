using Agent360.API.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Agent360.API.Configurations;

public static class AuthenticationConfiguration
{
    extension(WebApplicationBuilder webAppBuilder)
    {
        public WebApplicationBuilder ConfigureAuthentication()
        {
            var logtoOptions = webAppBuilder.Configuration.GetSection(LogtoOptions.LogtoSection).Get<LogtoOptions>();
            var authority = logtoOptions?.Authority.TrimEnd('/');
            var audience = logtoOptions?.Audience;

            var authenticationBuilder = webAppBuilder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

            // The template intentionally ships without authentication secrets or
            // placeholder identity-provider values. This keeps build-time OpenAPI
            // generation and health checks usable before environment configuration
            // is supplied. Protected endpoints require the real values at runtime.
            if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
            {
                return webAppBuilder;
            }

            var metadataAddress = authority.EndsWith(
                "/oidc",
                StringComparison.OrdinalIgnoreCase
            )
                ? $"{authority}/.well-known/openid-configuration"
                : $"{authority}/oidc/.well-known/openid-configuration";

            authenticationBuilder.AddJwtBearer(opts =>
                {
                    opts.Authority = authority;
                    opts.Audience = audience;
                    opts.MetadataAddress = metadataAddress;
                    opts.RequireHttpsMetadata = !webAppBuilder.Environment.IsDevelopment();

                    // Keep JWT claims as-is so identity services can read the Logto sub claim.
                    opts.MapInboundClaims = false;

                    opts.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.Zero
                    };
                });

            return webAppBuilder;
        }
    }
}