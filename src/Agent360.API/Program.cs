using Asp.Versioning;
using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Http.Authorization;
using Hmz.Core.SharedKernel.Identity.Abstractions;
using Agent360.API.Configurations;
using Agent360.API.Configurations.OpenApi;
using Agent360.API.Extensions;
using Agent360.API.Identity;
using Agent360.API.Middlewares;
using Agent360.ReferenceData.Infrastructure;
using Agent360.UserManagement.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

// Avoid application initialization is failed and the error can be logged due to Serilog is not configured yet.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateSlimBuilder(args);

    builder.WebHost.UseKestrelHttpsConfiguration();
    builder.WebHost.ConfigureKestrel(opts =>
    {
        opts.AddServerHeader = false;
    });

    builder.WebHost.UseDefaultServiceProvider((ctx, opts) =>
    {
        opts.ValidateScopes = ctx.HostingEnvironment.IsDevelopment();
        opts.ValidateOnBuild = true;
    });

    // Already configured by default, you can customize it if needed.
    //builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    //                     .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    //                     .AddEnvironmentVariables()
    //                     .AddUserSecrets<Program>();

    // Configure JSON options for minimal APIs, not for controller endpoints.
    builder.Services.Configure<JsonOptions>(opts =>
    {
        opts.SerializerOptions.WriteIndented = false;
        opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        opts.SerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
        opts.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;

        opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        opts.SerializerOptions.AddAdvancedSearchConverters();
        opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

    builder.Services.Configure<RouteOptions>(opts =>
    {
        opts.LowercaseUrls = true;
        opts.LowercaseQueryStrings = true;
        opts.AppendTrailingSlash = true;
    });

    // Only uses when working with Controller endpoints
    //builder.Services.Configure<ApiBehaviorOptions>(opts =>
    //{
    //    opts.SuppressModelStateInvalidFilter = true;
    //});

    // Clear the default logging providers
    builder.Logging.ClearProviders();
    builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));

    builder.AddServiceDefaults();

    builder.Services.AddApiVersioning(opts =>
        {
            opts.DefaultApiVersion = ApiVersion.Default;
            opts.AssumeDefaultVersionWhenUnspecified = true;
            opts.ReportApiVersions = true;
        })
        .AddApiExplorer(opts =>
        {
            opts.GroupNameFormat = "'v'VVV";
            opts.SubstituteApiVersionInUrl = true;
        })
        .ConfigureOpenApi();

    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.ConfigureOptions();
    builder.ConfigureEndpoints();

    //builder.Services.AddOutputCache(options =>
    //{
    //    options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromMinutes(10)));
    //});

    // Must be registered before the shared handler: IExceptionHandlers run in registration order.
    builder.Services.AddExceptionHandler<Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler>();
    builder.Services.AddHmzProblemDetails();
    builder.Services.AddValidation();

    builder.ConfigureAuthentication();
    builder.Services.AddPermissionAuthorization();
    builder.Services.AddAuthorization();

    builder.Services.AddDatabaseDeveloperPageExceptionFilter();

    builder.ConfigureCors();

    builder.Services.AddEndpoints(typeof(Program).Assembly);

    builder.Services.AddMediator(opts =>
    {
        opts.ServiceLifetime = ServiceLifetime.Scoped;
    });

    builder.Services
        .AddReferenceDataInfrastructure(builder.Configuration)
        .AddUserManagementInfrastructure(builder.Configuration);

    // Zero config for L1 only (in-proc or in-memory)
    builder.Services.AddHybridCache(opts =>
    {
        opts.MaximumPayloadBytes = 1024 * 1024; // 1MB
        opts.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),
            LocalCacheExpiration = TimeSpan.FromMinutes(5)
        };
    });


    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<CurrentUser>();
    builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());

    var app = builder.Build();
    // Test hosts suppress only optional Development demo initialization.
    // Their fixture applies real migrations; core seeding remains independent.
    // This flag never disables Staging/Production core seeding.
    var isTestHost = app.Configuration.GetValue<bool>("Testing:IsTestHost");
    app.MapDefaultEndpoints();

    app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        SuppressDiagnosticsCallback = _ => false
    });

    await app.ConfigureDevelopmentExperienceAsync(
        isTestHost: isTestHost);

    if (app.Environment.IsProduction())
    {
        app.UseHsts(); // Permanent set HSTS header to force HTTPS for all requests cached by browsers
        app.UseHttpsRedirection(); // Redirect HTTP requests to HTTPS
    }

    app.UseWebSockets();
    app.UseRouting(); // Noted: By default, it will run this middleware at the beginning of the pipeline
    app.UseCors();

    app.UseAuthentication();
    app.UseMiddleware<UserResolutionMiddleware>();
    app.UseAuthorization();

    var apiVersionSet = app.NewApiVersionSet()
        .HasApiVersion(ApiVersion.Default)
        .HasApiVersion(new(2, 0))
        .ReportApiVersions()
        .Build();

    var versionGroup = app.MapGroup("/api/v{version:apiVersion}")
        .WithApiVersionSet(apiVersionSet);

    app.MapEndpoints(versionGroup);

    //app.MapControllers(); This will be enabled if using controller endpoints
    // Staging / Production: CI applies every module's reviewed, idempotent
    // migration artifact before the container starts. Core system data
    // (roles, permissions, system users, languages, currencies) is still
    // required for the API to function — seed it here.
    // The call is idempotent and never runs MigrateAsync.
    // Functional tests own migration and seed timing in their Testing environment.
    // Staging/Production always seed core data after project-owned migration SQL/jobs.
    if (app.Environment.IsStaging() || app.Environment.IsProduction())
    {
        try
        {
            await ApplicationBootstrapper.SeedCoreAsync(app.Services, app.Lifetime.ApplicationStopping);
        }
        catch (Exception ex)
        {
            Log.Warning("Database core seeding skipped or unavailable: {Message}", ex.Message);
        }
    }

    app.Run();
}
catch (HostAbortedException)
{
    // EF Core design-time tooling aborts the host after it has resolved the
    // application's services. This is an expected, successful tooling exit.
}
catch (Exception ex)
{
    Log.Fatal("Application failed to start correctly {Error}", ex);
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Enables functional host tests without changing the top-level composition.
public partial class Program
{
}