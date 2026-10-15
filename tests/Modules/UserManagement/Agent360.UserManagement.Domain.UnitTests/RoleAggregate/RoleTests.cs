using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Exceptions;
using Xunit;

namespace Agent360.UserManagement.Domain.UnitTests.RoleAggregate;

public sealed class RoleTests
{
    [Fact]
    public void Create_NormalizesNameAndCode()
    {
        // Arrange

        // Action
        var role = Role.Create("  Billing manager  ", "  billing_manager  ");

        // Assertion
        role.Name.Should().Be("Billing manager");
        role.Code.Should().Be("BILLING_MANAGER");
        role.IsSystemRole.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ThrowsExpectedDomainError(string? name)
    {
        // Arrange
        Action action = () => Role.Create(name!, "VALID");

        // Action
        var exception = action.Should().Throw<RoleDomainException>().Which;

        // Assertion
        exception.Code.Should().Be(RoleErrorCodes.NameRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankCode_ThrowsExpectedDomainError(string? code)
    {
        // Arrange
        Action action = () => Role.Create("Valid", code!);

        // Action
        var exception = action.Should().Throw<RoleDomainException>().Which;

        // Assertion
        exception.Code.Should().Be(RoleErrorCodes.CodeRequired);
    }

    [Fact]
    public void Create_WithOverlengthValues_ThrowsExpectedDomainErrors()
    {
        // Arrange
        Action nameAction = () =>
            Role.Create(new string('n', Role.MaxNameLength + 1), "VALID");
        Action codeAction = () =>
            Role.Create("Valid", new string('c', Role.MaxCodeLength + 1));

        // Action
        var nameException = nameAction.Should().Throw<RoleDomainException>().Which;
        var codeException = codeAction.Should().Throw<RoleDomainException>().Which;

        // Assertion
        nameException.Code.Should().Be(RoleErrorCodes.NameTooLong);
        codeException.Code.Should().Be(RoleErrorCodes.CodeTooLong);
    }

    [Fact]
    public void Update_NormalizesMutableRole()
    {
        // Arrange
        var role = Role.Create("Original", "ORIGINAL");

        // Action
        role.Update("  Updated  ", "  updated_code  ");

        // Assertion
        role.Name.Should().Be("Updated");
        role.Code.Should().Be("UPDATED_CODE");
    }

    [Fact]
    public void SystemRole_RejectsAllMutationAndDeletionPaths()
    {
        // Arrange
        var role = Role.Create("System", "SYSTEM", isSystemRole: true);
        Action renameAction = () => role.Rename("Renamed");
        Action updateAction = () => role.Update("Updated", "UPDATED");
        Action deleteAction = () => role.EnsureCanBeDeleted();

        // Action
        var renameException = renameAction.Should().Throw<RoleDomainException>().Which;
        var updateException = updateAction.Should().Throw<RoleDomainException>().Which;
        var deleteException = deleteAction.Should().Throw<RoleDomainException>().Which;

        // Assertion
        renameException.Code.Should().Be(RoleErrorCodes.SystemRoleImmutable);
        updateException.Code.Should().Be(RoleErrorCodes.SystemRoleImmutable);
        deleteException.Code.Should().Be(RoleErrorCodes.SystemRoleImmutable);
    }
}