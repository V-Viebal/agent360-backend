using Agent360.UserManagement.Domain.Exceptions;

namespace Agent360.UserManagement.Domain.RoleAggregate.Exceptions;

public sealed class RoleDomainException(string code, string message)
    : DomainException(code, message);