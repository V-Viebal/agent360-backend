using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.UserManagement.UseCase.Roles.Common;
using System.Net;

namespace Agent360.API.FunctionalTests.Roles;

public sealed class RolesEndpointTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task SearchRoles_WithAdminSubject_HydratesPermissionsBeforeAuthorization()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        // Action
        using var response = await Api.SearchRolesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        payload.Should().NotBeNull();
        payload!.Items.Should().Contain(role => role.Code == RoleCodes.Admin);
    }

    [Fact]
    public async Task SearchRoles_WithCustomerSubject_ReturnsForbidden()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.Customer);

        // Action
        using var response = await Api.SearchRolesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SearchRoles_AppliesPostgreSqlFilterSortAndPagination()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        await Api.CreateRoleAndReadAsync("Auditor", "AUDITOR", TestContext.Current.CancellationToken);
        await Api.CreateRoleAndReadAsync("Account Manager", "ACCOUNT", TestContext.Current.CancellationToken);

        var request = new AdvancedSearchRequest
        {
            Search =
            [
                new GlobalSearchItem
                {
                    SearchFields = ["Name", "Code"],
                    SearchValue = "a",
                    Operator = SearchOperator.Contains
                }
            ],
            Filter = new FilterGroup
            {
                Filters =
                [
                    new FilterItem
                    {
                        SearchField = "IsSystemRole",
                        SearchValues = ["false"],
                        Operator = SearchOperator.Eq
                    }
                ]
            },
            OrderBy =
            [
                new SearchOrder { Field = "Name", Direction = SortDirection.Desc },
                new SearchOrder { Field = "Id", Direction = SortDirection.Asc }
            ],
            Page = 2,
            Limit = 1
        };

        // Action
        using var response = await Api.SearchRolesAsync(
            request,
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        payload.Should().NotBeNull();
        payload!.Page.Should().Be(2);
        payload.PageSize.Should().Be(1);
        payload.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchRoles_ById_ParsesClientIntegerOnPostgreSql()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        var target = await Api.CreateRoleAndReadAsync(
            "Identity-search target",
            "ID-SEARCH",
            TestContext.Current.CancellationToken);

        // Action
        using var response = await Api.SearchRolesAsync(
            new AdvancedSearchRequest
            {
                Search =
                [
                    new GlobalSearchItem
                    {
                        SearchFields = ["Id", "Name", "Code"],
                        SearchValue = target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Operator = SearchOperator.Contains
                    }
                ]
            },
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page!.Items.Should().ContainSingle().Which.Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task GetRole_WithCustomerSubject_ReturnsForbidden()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.ExternalId, RoleCodes.Customer);

        // Action
        using var response = await Api.GetRoleAsync(1, TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateRole_WithDuplicateCode_ReturnsConflict()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        await Api.CreateRoleAndReadAsync(
            "Existing role", "SAME-CODE", TestContext.Current.CancellationToken);

        // Action
        using var response = await Api.CreateRoleAsync(
            "Duplicate role", "SAME-CODE", TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateGetUpdateDeleteRole_UsesStrongPreconditions()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        // Action
        using var createResponse = await Api.CreateRoleAsync(
            "Support operator",
            "SUPPORT",
            TestContext.Current.CancellationToken);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdEtag = createResponse.RequireEntityTag();
        var created = await createResponse.ReadRequiredJsonAsync<RoleResponse>(
            HttpStatusCode.Created,
            cancellationToken: TestContext.Current.CancellationToken);

        createResponse.Headers.Location.Should().NotBeNull();
        createResponse.Headers.Location!.AbsolutePath.TrimEnd('/').Should()
            .Be($"/api/v1/roles/{created.Id}");
        created.Should().NotBeNull();

        using var getResponse = await Api.GetRoleAsync(
            created!.Id,
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.RequireEntityTag().Tag.Should().Be(createdEtag.Tag);

        _client.DefaultRequestHeaders.IfNoneMatch.Clear();
        _client.DefaultRequestHeaders.IfNoneMatch.Add(createdEtag);
        using var notModifiedResponse = await Api.GetRoleAsync(
            created.Id,
            TestContext.Current.CancellationToken);

        notModifiedResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
        _client.DefaultRequestHeaders.IfNoneMatch.Clear();

        using var missingPreconditionResponse = await Api.UpdateRoleAsync(
            created.Id,
            "Support lead",
            "SUPPORT-LEAD",
            TestContext.Current.CancellationToken);

        missingPreconditionResponse.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);

        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(createdEtag);
        using var updateResponse = await Api.UpdateRoleAsync(
            created.Id,
            "Support lead",
            "SUPPORT-LEAD",
            TestContext.Current.CancellationToken);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedEtag = updateResponse.RequireEntityTag();

        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(updatedEtag);
        using var deleteResponse = await Api.DeleteRoleAsync(
            created.Id,
            TestContext.Current.CancellationToken);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _client.DefaultRequestHeaders.IfMatch.Clear();

        using var deletedGetResponse = await Api.GetRoleAsync(
            created.Id,
            TestContext.Current.CancellationToken);

        // Assertion
        deletedGetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}