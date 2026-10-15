using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Authorization;
using Hmz.Core.SharedKernel.Data.Extensions;
using Agent360.UserManagement.Infrastructure.Authorization;
using Agent360.UserManagement.Infrastructure.Identity;
using Agent360.UserManagement.Infrastructure.Persistence;
using Agent360.UserManagement.Infrastructure.Persistence.Queries;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Common;
using Agent360.UserManagement.UseCase.Users.Abstractions;
using Agent360.UserManagement.UseCase.Users.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent360.UserManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddUserManagementInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddPersistenceInterceptors();
        services.AddScoped<IPermissionSnapshotAccessor, PermissionSnapshotAccessor>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<PermissionSnapshotResolver>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<AppDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "user_management");
            })
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

            opts.RegisterPersistenceInterceptors(sp);
        });

        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>))
            .AddScoped(typeof(IReadRepository<>), typeof(EfRepository<>))
            .AddScoped<IUserManagementUnitOfWork, UserManagementUnitOfWork>();

        services.AddAdvancedSearch();
        services.AddScoped<IAdvancedSearchQuery<RoleResponse>, RoleAdvancedSearchQuery>();
        services.AddScoped<IAdvancedSearchQuery<UserResponse>, UserAdvancedSearchQuery>();
        services.AddScoped<IPermissionQueryService, PermissionQueryService>();
        services.AddScoped<IPermissionCacheInvalidator, PermissionCacheInvalidator>();

        services.AddSingleton<IUserIdentityService, UserIdentityService>();
        services.AddSingleton<IRolePersistenceErrorClassifier, PostgresRolePersistenceErrorClassifier>();
        services.AddSingleton<IUserPersistenceErrorClassifier, PostgresUserPersistenceErrorClassifier>();
        return services;
    }
}