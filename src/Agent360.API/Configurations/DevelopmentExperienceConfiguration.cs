using Agent360.API.Options;
using Scalar.AspNetCore;

namespace Agent360.API.Configurations;

public static class DevelopmentExperienceConfiguration
{
    extension(WebApplication app)
    {
        public async Task ConfigureDevelopmentExperienceAsync(
            CancellationToken cancellationToken = default,
            bool isTestHost = false
        )
        {
            if (app.Environment.IsDevelopment())
            {
                try
                {
                    // Local Development seeds core data after an explicit per-module
                    // migration operation. A test host suppresses only demo data.
                    await ApplicationBootstrapper.SeedCoreAsync(app.Services, cancellationToken);

                    if (!isTestHost)
                    {
                        await ApplicationBootstrapper.SeedDevelopmentAsync(app.Services, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning(ex, "Skipping database seeding because local PostgreSQL is not reachable at 127.0.0.1:5432. Agent360 API endpoints and QwenPaw infra proxies will continue running.");
                }
            }

            if (!SupportsInteractiveApiDocumentation(app.Environment))
            {
                return;
            }

            app.MapOpenApi()
               .WithDocumentPerVersion()
               .AllowAnonymous();

            app.MapScalarApiReference(options =>
                {
                    ConfigureDocuments(app, options);
                    ConfigureAuthentication(app, options);
                })
                .AllowAnonymous();

            app.MapGet("/", () => Results.Redirect("/scalar"))
                .AllowAnonymous()
                .ExcludeFromDescription();
        }
    }

    private static bool SupportsInteractiveApiDocumentation(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsStaging();

    private static void ConfigureDocuments(WebApplication app, ScalarOptions options)
    {
        var descriptions = app.DescribeApiVersions();

        for (var index = 0; index < descriptions.Count; index++)
        {
            var description = descriptions[index];

            options.AddDocument(
                description.GroupName,
                description.GroupName,
                isDefault: index == 0
            );
        }

        options.Title = "Agent360 API Reference";
    }

    private static void ConfigureAuthentication(WebApplication app, ScalarOptions options)
    {
        var logtoOptions = app
            .Configuration.GetSection(LogtoOptions.LogtoSection)
            .Get<LogtoOptions>();

        options
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
            .WithOperationTitleSource(OperationTitleSource.Path)
            .AddPreferredSecuritySchemes("OAuth2")
            .AddAuthorizationCodeFlow(
                "OAuth2",
                flow =>
                {
                    flow.ClientId = logtoOptions?.OpenAPIClientId;
                    flow.Pkce = Pkce.Sha256;
                    flow.RedirectUri = logtoOptions?.OpenAPIRedirectUri;

                    if (Uri.TryCreate(logtoOptions?.Audience, UriKind.Absolute, out _))
                    {
                        flow.AddQueryParameter("resource", logtoOptions.Audience);
                        flow.AddBodyParameter("resource", logtoOptions.Audience);
                    }

                    flow.SelectedScopes = logtoOptions?.Scopes;
                }
            )
            .EnablePersistentAuthentication();
    }
}
