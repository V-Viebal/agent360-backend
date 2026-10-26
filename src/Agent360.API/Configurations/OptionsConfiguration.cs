using Agent360.API.Options;

namespace Agent360.API.Configurations;

public static class OptionsConfiguration
{
    extension(WebApplicationBuilder webApplicationBuilder)
    {
        public WebApplicationBuilder ConfigureOptions()
        {
            var validateRuntimeConfiguration =
                !webApplicationBuilder.Environment.IsDevelopment()
                && webApplicationBuilder.Configuration.GetValue<bool>(
                    "RuntimeConfiguration:Validate"
                );

            webApplicationBuilder.Services.AddOptions<LogtoOptions>()
                .BindConfiguration(LogtoOptions.LogtoSection)
                .Validate(
                    options =>
                        !validateRuntimeConfiguration
                        || (
                            IsHttpsUrl(options.Authority)
                            && IsHttpsUrl(options.Audience)
                            && !string.IsNullOrWhiteSpace(options.Webhooks.SignUpSigningKey)
                            && (
                                !webApplicationBuilder.Environment.IsStaging()
                                || (
                                    !string.IsNullOrWhiteSpace(options.OpenAPIClientId)
                                    && IsHttpsUrl(options.OpenAPIRedirectUri)
                                )
                            )
                        ),
                    "Logto runtime configuration is invalid."
                )
                .ValidateOnStart();

            webApplicationBuilder.Services.AddOptions<CorsOptions>()
                .Configure<IConfiguration>((o, cfg) => o.AllowedOrigins = CorsOptions.Read(cfg))
                .Validate(
                    options =>
                        !validateRuntimeConfiguration
                        || options.AllowedOrigins.Length > 0
                            && options.AllowedOrigins.All(IsHttpOrigin),
                    "Cors:AllowedOrigins must contain explicit HTTP(S) origins."
                )
                .ValidateOnStart();

            webApplicationBuilder.Services.AddOptions<StorageOptions>()
                .BindConfiguration(StorageOptions.StorageSection);

            webApplicationBuilder.Services.AddOptions<QwenPawOptions>()
                .BindConfiguration(QwenPawOptions.Section);

            webApplicationBuilder.Services.AddOptions<ComposioOptions>()
                .BindConfiguration(ComposioOptions.SectionName);

            return webApplicationBuilder;
        }
    }

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsHttpOrigin(string value) =>
        value != "*"
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
