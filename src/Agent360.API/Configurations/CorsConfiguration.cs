using Agent360.API.Options;

namespace Agent360.API.Configurations;

public static class CorsConfiguration
{
    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder ConfigureCors()
        {
            var allowedOrigins =
                builder.Configuration.GetSection(CorsOptions.Section).Get<CorsOptions>()?.AllowedOrigins
                ?? [];

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    if (builder.Environment.IsDevelopment() && allowedOrigins.Length == 0)
                    {
                        policy.AllowAnyOrigin()
                            .AllowAnyHeader()
                            .AllowAnyMethod();

                        return;
                    }

                    policy
                        .WithOrigins(allowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
            });

            return builder;
        }
    }
}