using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.UseCase.Roles.Common;
using Xunit;

namespace Agent360.UserManagement.UseCase.UnitTests.Roles.Common;

public sealed class RoleResponseTests
{
    [Fact]
    public void From_MapsNormalizedRoleValues()
    {
        // Arrange
        var role = Role.Create("  Administrator  ", "  admin  ");

        // Action
        var response = RoleResponse.From(role);

        // Assertion
        response.Id.Should().Be(role.Id);
        response.Name.Should().Be("Administrator");
        response.Code.Should().Be("ADMIN");
        response.IsSystemRole.Should().BeFalse();
        response.RowVersion.Should().Be(role.RowVersion);
    }
}