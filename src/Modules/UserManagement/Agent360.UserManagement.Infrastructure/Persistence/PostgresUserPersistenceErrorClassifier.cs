using Agent360.UserManagement.UseCase.Users.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence;

internal sealed class PostgresUserPersistenceErrorClassifier
    : IUserPersistenceErrorClassifier
{
    public bool IsConcurrencyConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException;
}