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
                // Run seeding in background task to avoid blocking Kestrel HTTP startup
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                        await ApplicationBootstrapper.SeedCoreAsync(app.Services, linkedCts.Token);

                        if (!isTestHost)
                        {
                            await ApplicationBootstrapper.SeedDevelopmentAsync(app.Services, linkedCts.Token);
                        }
                    }
                    catch (Exception)
                    {
                        app.Logger.LogInformation("Local PostgreSQL 127.0.0.1:5432 not reachable. Agent360 API endpoints continue in direct AgentCore infra mode.");
                    }
                });
            }

            if (!SupportsInteractiveApiDocumentation(app.Environment, app.Configuration))
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

    // The OpenAPI document maps every route (incl. operator-guarded writes): only Development exposes it by default.
    // Staging/Production can opt in with Agent360:Security:ExposeApiReference=true.
    internal static bool SupportsInteractiveApiDocumentation(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() || configuration.GetValue("Agent360:Security:ExposeApiReference", false);

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
