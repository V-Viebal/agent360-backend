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
                else
                {
                    // Fallback configuration when Logto is not configured on this environment
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
            });

            return webAppBuilder;
        }
    }
}
