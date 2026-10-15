using Ardalis.SmartEnum;

namespace Agent360.UserManagement.Domain.UserAggregate.Enums;

public class UserStatus : SmartEnum<UserStatus>
{
    public static readonly UserStatus Active = new(nameof(Active), 1);
    public static readonly UserStatus Inactive = new(nameof(Inactive), 2);

    protected UserStatus(string name, int value) : base(name, value) { }
}