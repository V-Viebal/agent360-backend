using Hmz.Core.SharedKernel.Data;

namespace Agent360.UserManagement.UseCase.Abstractions;

/// <summary>
/// The User Management module's save-changes boundary.
/// Keeping this interface in the use-case layer prevents it from depending on EF Core
/// or another module's persistence implementation.
/// </summary>
public interface IUserManagementUnitOfWork : IUnitOfWork
{
}