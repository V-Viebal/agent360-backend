using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Data;

namespace Agent360.UserManagement.Infrastructure.Persistence;

public sealed class EfRepository<T>(AppDbContext dbContext)
    : EfRepositoryBase<T>(dbContext)
    where T : class, IAggregateRoot
{
}