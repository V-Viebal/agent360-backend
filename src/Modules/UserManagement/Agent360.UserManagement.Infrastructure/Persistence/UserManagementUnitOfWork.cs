using Hmz.Core.SharedKernel.Data;
using Agent360.UserManagement.UseCase.Abstractions;

namespace Agent360.UserManagement.Infrastructure.Persistence;

/// <summary>
/// User Management's EF Core unit of work.
/// </summary>
public sealed class UserManagementUnitOfWork(AppDbContext dbContext)
    : EfUnitOfWork<AppDbContext>(dbContext), IUserManagementUnitOfWork
{
}