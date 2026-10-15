using Hmz.Core.SharedKernel.Data.Extensions;
using Agent360.ReferenceData.Infrastructure.Persistence;
using Agent360.ReferenceData.Infrastructure.Persistence.Queries;
using Agent360.ReferenceData.UseCase.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent360.ReferenceData.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReferenceDataInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPersistenceInterceptors();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<AppDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "reference_data");
            })
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

            opts.RegisterPersistenceInterceptors(sp);
        });

        services.AddScoped<IGetCurrenciesQueryService, GetCurrenciesQueryService>();
        services.AddScoped<IGetLanguagesQueryService, GetLanguagesQueryService>();

        return services;
    }
}