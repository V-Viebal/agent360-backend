namespace Agent360.UserManagement.UseCase.Roles.Abstractions;

public interface IRolePersistenceErrorClassifier
{
    bool IsCodeUniqueViolation(Exception exception);

    bool IsConcurrencyConflict(Exception exception);
}