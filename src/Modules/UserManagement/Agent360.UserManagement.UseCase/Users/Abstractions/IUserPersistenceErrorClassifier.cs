namespace Agent360.UserManagement.UseCase.Users.Abstractions;

public interface IUserPersistenceErrorClassifier
{
    bool IsConcurrencyConflict(Exception exception);
}