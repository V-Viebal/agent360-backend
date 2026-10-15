using Agent360.UserManagement.UseCase.Roles.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agent360.UserManagement.Infrastructure.Persistence;

internal sealed class PostgresRolePersistenceErrorClassifier
    : IRolePersistenceErrorClassifier
{
    private const string RoleCodeUniqueConstraint = "IX_Roles_Code";

    public bool IsCodeUniqueViolation(Exception exception) =>
        exception is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: RoleCodeUniqueConstraint
            }
        };

    public bool IsConcurrencyConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException;
}