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
                .AddAuthentication(opts =>
                {
                    opts.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    opts.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });

            // Always register JwtBearer handler to satisfy default challenge/authenticate schemes
            authenticationBuilder.AddJwtBearer(opts =>
            {
                if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience))
                {
                    var metadataAddress = authority.EndsWith(
                        "/oidc",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? $"{authority}/.well-known/openid-configuration"
                        : $"{authority}/oidc/.well-known/openid-configuration";

                    opts.Authority = authority;
                    opts.Audience = audience;
                    opts.MetadataAddress = metadataAddress;
                    opts.RequireHttpsMetadata = !webAppBuilder.Environment.IsDevelopment();
                    opts.MapInboundClaims = false;
                    opts.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.Zero
                    };
                }
                else if (webAppBuilder.Environment.IsDevelopment())
                {
                    // Local development without Logto: accept unsigned tokens so the dev UI can exercise auth paths.
                    opts.RequireHttpsMetadata = false;
                    opts.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = false,
                        ValidateIssuerSigningKey = false,
                        SignatureValidator = (token, parameters) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token)
                    };
                }
                else
                {
                    // Fail closed outside Development: without an authority no bearer token can ever authenticate
                    // (previously any forged/unsigned JWT was accepted when Logto was not configured).
                    opts.RequireHttpsMetadata = true;
                    opts.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        IssuerSigningKeys = [],
                        ValidIssuer = "urn:agent360:logto-not-configured",
                        ValidAudience = "urn:agent360:logto-not-configured",
                    };
                }
            });

            return webAppBuilder;
        }
    }
}
