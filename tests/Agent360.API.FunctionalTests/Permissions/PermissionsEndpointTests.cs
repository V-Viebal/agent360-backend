using Agent360.UserManagement.UseCase.Permissions.Common;
using Agent360.UserManagement.UseCase.Roles.Common;
using System.Net;

namespace Agent360.API.FunctionalTests.Permissions;

public sealed class PermissionsEndpointTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task ListPermissions_WithAdmin_ReturnsCatalogAndStrongEntityTag()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        // Action
        using var response = await Api.ListPermissionsAsync(TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.RequireEntityTag();

        var permissions = await response.ReadRequiredJsonAsync<IReadOnlyList<PermissionResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        permissions.Should().NotBeNull();
        permissions!.Select(permission => permission.Code).Should().Contain(UserManagementPermissions.Roles.Manage);
        var manageUserRoles = permissions.Single(permission =>
            permission.Code == UserManagementPermissions.Users.ManageRoles);
        manageUserRoles.Roles.Should().ContainSingle()
            .Which.Code.Should().Be(RoleCodes.Admin);
    }

    [Fact]
    public async Task ListPermissions_WithCustomer_ReturnsForbidden()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.Customer);

        // Action
        using var response = await Api.ListPermissionsAsync(TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAndReplaceRolePermissions_WithAdmin_PersistsSelection()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        var role = await Api.CreateRoleAndReadAsync(
            "Permission target",
            "PERMISSION-TARGET",
            TestContext.Current.CancellationToken);

        using var catalogResponse = await Api.ListPermissionsAsync(TestContext.Current.CancellationToken);
        var permissions = await catalogResponse.ReadRequiredJsonAsync<IReadOnlyList<PermissionResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var selectedPermission = permissions!
            .Single(permission => permission.Code == UserManagementPermissions.Roles.Read);

        // Action
        using var getResponse = await Api.GetRolePermissionsAsync(
            role.Id,
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = getResponse.RequireEntityTag();

        var initial = await getResponse.ReadRequiredJsonAsync<RolePermissionsResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        initial.Should().NotBeNull();
        initial!.Permissions.Should().BeEmpty();

        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(etag);

        using var replaceResponse = await Api.ReplaceRolePermissionsAsync(
            role.Id,
            [selectedPermission.Id],
            TestContext.Current.CancellationToken);

        // Assertion
        replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replaceResponse.RequireEntityTag();

        var replaced = await replaceResponse.ReadRequiredJsonAsync<RolePermissionsResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        replaced.Should().NotBeNull();
        replaced!.Permissions.Should().ContainSingle()
            .Which.Code.Should().Be(UserManagementPermissions.Roles.Read);
    }

    [Fact]
    public async Task ReplaceRolePermissions_WithAdminOnlyPermission_ReturnsValidationProblem()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        var role = await Api.CreateRoleAndReadAsync(
            "Delegated administrator",
            "DELEGATED-ADMIN",
            TestContext.Current.CancellationToken);
        using var catalogResponse = await Api.ListPermissionsAsync(
            TestContext.Current.CancellationToken);
        var permissions = await catalogResponse.ReadRequiredJsonAsync<IReadOnlyList<PermissionResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);
        var adminOnlyPermission = permissions.Single(permission =>
            permission.Code == UserManagementPermissions.Users.ManageRoles);
        using var getResponse = await Api.GetRolePermissionsAsync(
            role.Id,
            TestContext.Current.CancellationToken);
        var etag = getResponse.RequireEntityTag();
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(etag);

        // Action
        using var response = await Api.ReplaceRolePermissionsAsync(
            role.Id,
            [adminOnlyPermission.Id],
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))
            .Should().Contain(RoleApplicationErrorCodes.AdminOnlyPermission);
    }

    [Fact]
    public async Task ReplaceAdminPermissions_WithoutRoleManagementPermission_ReturnsValidationProblem()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var rolesResponse = await Api.SearchRolesAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var roles = await rolesResponse.ReadRequiredJsonAsync<
            Hmz.Core.SharedKernel.Contracts.Pagination.PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);
        var adminRole = roles.Items.Single(role => role.Code == RoleCodes.Admin);

        using var catalogResponse = await Api.ListPermissionsAsync(
            TestContext.Current.CancellationToken);
        var permissions = await catalogResponse.ReadRequiredJsonAsync<IReadOnlyList<PermissionResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);
        var roleManagementPermissionId = permissions.Single(permission =>
            permission.Code == UserManagementPermissions.Users.ManageRoles).Id;

        using var getResponse = await Api.GetRolePermissionsAsync(
            adminRole.Id,
            TestContext.Current.CancellationToken);
        var current = await getResponse.ReadRequiredJsonAsync<RolePermissionsResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        var etag = getResponse.RequireEntityTag();
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(etag);

        // Action
        using var response = await Api.ReplaceRolePermissionsAsync(
            adminRole.Id,
            current.Permissions
                .Where(permission => permission.Id != roleManagementPermissionId)
                .Select(permission => permission.Id)
                .ToArray(),
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))
            .Should().Contain(RoleApplicationErrorCodes.RequiredAdminPermission);
    }
}