using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Permissions.Common;
using Agent360.UserManagement.UseCase.Roles.Common;
using Agent360.UserManagement.UseCase.Users.Common;
using Agent360.UserManagement.UseCase.Users.Queries.GetCurrentProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent360.API.FunctionalTests.Users;

public sealed class UsersEndpointTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task GetCurrentProfile_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange
        // The shared client intentionally has no authentication header.

        // Action
        using var response = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCurrentProfile_ForAdminSubject_ReflectsDatabaseRoleAndPermissions()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        // Action
        using var response = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.ReadRequiredJsonAsync<CurrentUserProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        profile.Should().NotBeNull();
        profile!.ExternalUserId.Should().Be(TestActors.AdminSubject);
        profile.Roles.Should().ContainSingle(role => role.Code == RoleCodes.Admin);
        profile.Permissions.Should().Contain(UserManagementPermissions.Roles.Read);
    }

    [Fact]
    public async Task GetCurrentProfile_WithMatchingEntityTag_ReturnsNotModified()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var initialResponse = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);
        var entityTag = initialResponse.RequireEntityTag();

        _client.DefaultRequestHeaders.IfNoneMatch.Clear();
        _client.DefaultRequestHeaders.IfNoneMatch.Add(entityTag);

        // Action
        using var response = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        response.RequireEntityTag().Tag.Should().Be(entityTag.Tag);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoCache.Should().BeTrue();
        response.Headers.Vary.Should().Contain("Authorization");

        (await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetCurrentProfile_AfterProfileChange_InvalidatesEntityTag()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);
        using var initialResponse = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);
        var originalEntityTag = initialResponse.RequireEntityTag();

        await using (var scope = Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<
                Agent360.UserManagement.Infrastructure.Persistence.AppDbContext>();

            await db.Users
                .Where(user => user.LogtoUserId == TestActors.CustomerSubject)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.FirstName,
                        "Updated"),
                    TestContext.Current.CancellationToken);
        }

        _client.DefaultRequestHeaders.IfNoneMatch.Clear();
        _client.DefaultRequestHeaders.IfNoneMatch.Add(originalEntityTag);

        // Action
        using var response = await Api.GetCurrentUserAsync(
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.RequireEntityTag().Tag.Should().NotBe(originalEntityTag.Tag);
        var updated = await response.ReadRequiredJsonAsync<CurrentUserProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        updated.FirstName.Should().Be("Updated");
    }

    [Fact]
    public async Task GetCurrentProfile_AfterRoleAssignmentChange_InvalidatesEntityTag()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);
        using var initialResponse = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        var initialProfile = await initialResponse.ReadRequiredJsonAsync<CurrentUserProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        var originalEntityTag = initialResponse.RequireEntityTag();

        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var rolesResponse = await Api.SearchRolesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var roles = await rolesResponse.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var adminRole = roles.Items.Single(role => role.Code == RoleCodes.Admin);
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(
            new System.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{initialProfile.RowVersion}\""));

        using var replaceResponse = await Api.ReplaceUserRolesAsync(
            initialProfile.Id,
            [adminRole.Id],
            TestContext.Current.CancellationToken);

        replaceResponse.EnsureSuccessStatusCode();

        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Admin);
        _client.DefaultRequestHeaders.IfNoneMatch.Clear();
        _client.DefaultRequestHeaders.IfNoneMatch.Add(originalEntityTag);

        // Action
        using var response = await Api.GetCurrentUserAsync(
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.RequireEntityTag().Tag.Should().NotBe(originalEntityTag.Tag);
        var updated = await response.ReadRequiredJsonAsync<CurrentUserProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        updated.Roles.Should().ContainSingle(role => role.Code == RoleCodes.Admin);
    }

    [Fact]
    public async Task GetCurrentProfile_AfterRolePermissionChange_InvalidatesEntityTag()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);
        using var initialResponse = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);
        var originalEntityTag = initialResponse.RequireEntityTag();

        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var rolesResponse = await Api.SearchRolesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var roles = await rolesResponse.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var customerRole = roles.Items.Single(role => role.Code == RoleCodes.Customer);
        using var rolePermissionsResponse = await Api.GetRolePermissionsAsync(
            customerRole.Id,
            TestContext.Current.CancellationToken);

        var rolePermissions = await rolePermissionsResponse.ReadRequiredJsonAsync<RolePermissionsResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        var roleEntityTag = rolePermissionsResponse.RequireEntityTag();
        using var catalogResponse = await Api.ListPermissionsAsync(
            TestContext.Current.CancellationToken);

        var permissions = await catalogResponse.ReadRequiredJsonAsync<
            IReadOnlyList<PermissionResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var additionalPermission = permissions.Single(permission =>
            permission.Code == UserManagementPermissions.Roles.Read);

        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(roleEntityTag);

        using var replaceResponse = await Api.ReplaceRolePermissionsAsync(
            customerRole.Id,
            rolePermissions.Permissions
                .Select(permission => permission.Id)
                .Append(additionalPermission.Id)
                .Distinct()
                .ToArray(),
            TestContext.Current.CancellationToken);

        replaceResponse.EnsureSuccessStatusCode();

        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);
        _client.DefaultRequestHeaders.IfNoneMatch.Clear();
        _client.DefaultRequestHeaders.IfNoneMatch.Add(originalEntityTag);

        // Action
        using var response = await Api.GetCurrentUserAsync(
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.RequireEntityTag().Tag.Should().NotBe(originalEntityTag.Tag);
        var profile = await response.ReadRequiredJsonAsync<CurrentUserProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        profile.Permissions.Should().Contain(UserManagementPermissions.Roles.Read);
    }

    [Fact]
    public async Task GetCurrentProfile_ForUnknownSubject_ReturnsNotFound()
    {
        // Arrange
        Api.AuthenticateAs("not-provisioned", RoleCodes.Customer);

        // Action
        using var response = await Api.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignUpWebhook_WithoutSignature_ReturnsUnauthorized()
    {
        // Arrange
        var payload = CreateWebhookPayload("unsigned-user", "unsigned@example.test");

        // Action
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/users/webhooks/logto/signup",
            payload,
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignUpWebhook_WithInvalidSignature_ReturnsUnauthorized()
    {
        // Arrange
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/users/webhooks/logto/signup")
        {
            Content = JsonContent.Create(CreateWebhookPayload(
                "invalid-signature-user",
                "invalid@example.test"))
        };

        request.Headers.Add(LogtoDefaults.WebhookSignatureHeaderKey, "invalid");

        // Action
        using var response = await _client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignUpWebhook_WithValidSignature_CreatesCustomerUser()
    {
        // Arrange
        var payload = CreateWebhookPayload("logto-functional-user", "functional@example.test");
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/users/webhooks/logto/signup")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Add(LogtoDefaults.WebhookSignatureHeaderKey, ComputeSignature(json));

        // Action
        using var response = await _client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken)).Should().BeEmpty();

        Api.AuthenticateAs("logto-functional-user", RoleCodes.Customer);
        using var profileResponse = await Api.GetCurrentUserAsync(
            TestContext.Current.CancellationToken);

        // Assertion
        profileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await profileResponse.ReadRequiredJsonAsync<CurrentUserProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        profile.Should().NotBeNull();
        profile!.Email.Should().Be("functional@example.test");
        profile.Roles.Should().ContainSingle(role => role.Code == RoleCodes.Customer);
    }

    [Fact]
    public async Task SearchUsers_WithAdmin_ReturnsUsersAndTheirRoles()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        // Action
        using var response = await Api.SearchUsersAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Assertion
        var users = await response.ReadRequiredJsonAsync<PagedResponse<UserResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);
        users.Items.Should().Contain(user =>
            user.ExternalUserId == TestActors.AdminSubject &&
            user.Roles.Any(role => role.Code == RoleCodes.Admin));
        users.Items.Should().Contain(user =>
            user.ExternalUserId == TestActors.CustomerSubject &&
            user.Roles.Any(role => role.Code == RoleCodes.Customer));
    }

    [Fact]
    public async Task SearchUsers_WithDefaultRequest_UsesDocumentedDefaults()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);

        // Action
        using var response = await Api.SearchUsersAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Assertion
        var users = await response.ReadRequiredJsonAsync<PagedResponse<UserResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);
        users.Page.Should().Be(1);
        users.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task SearchUsers_AppliesPostgreSqlSearchFilterSortAndPagination()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        var request = new AdvancedSearchRequest
        {
            Search =
            [
                new GlobalSearchItem
                {
                    SearchFields = ["Email", "FirstName", "LastName", "DisplayName"],
                    SearchValue = "Functional",
                    Operator = SearchOperator.Contains
                }
            ],
            Filter = new FilterGroup
            {
                Filters =
                [
                    new FilterItem
                    {
                        SearchField = "ExternalUserId",
                        SearchValues = ["functional-"],
                        Operator = SearchOperator.StartsWith
                    }
                ]
            },
            OrderBy =
            [
                new SearchOrder { Field = "Email", Direction = SortDirection.Desc },
                new SearchOrder { Field = "Id", Direction = SortDirection.Asc }
            ],
            Page = 2,
            Limit = 1
        };

        // Action
        using var response = await Api.SearchUsersAsync(
            request,
            TestContext.Current.CancellationToken);

        // Assertion
        var users = await response.ReadRequiredJsonAsync<PagedResponse<UserResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);
        users.TotalCount.Should().Be(2);
        users.Items.Should().ContainSingle();
        users.Items[0].Roles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchUsers_WithUnknownField_ReturnsValidationProblem()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        var request = new AdvancedSearchRequest
        {
            Filter = new FilterGroup
            {
                Filters =
                [
                    new FilterItem
                    {
                        SearchField = "Roles",
                        SearchValues = ["ADMIN"],
                        Operator = SearchOperator.Eq
                    }
                ]
            }
        };

        // Action
        using var response = await Api.SearchUsersAsync(
            request,
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))
            .Should().Contain(UserApplicationErrorCodes.InvalidSearch);
    }

    [Fact]
    public async Task SearchUsers_WithCustomer_ReturnsForbidden()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);

        // Action
        using var response = await Api.SearchUsersAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReplaceUserRoles_WithAdmin_ReplacesRolesAndInvalidatesPermissionSnapshot()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var usersResponse = await Api.SearchUsersAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var users = await usersResponse.ReadRequiredJsonAsync<PagedResponse<UserResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var customer = users.Items.Single(
            user => user.ExternalUserId == TestActors.CustomerSubject);

        using var rolesResponse = await Api.SearchRolesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var roles = await rolesResponse.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var adminRole = roles.Items.Single(role => role.Code == RoleCodes.Admin);

        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);
        using var forbiddenBefore = await Api.ListPermissionsAsync(
            TestContext.Current.CancellationToken);

        forbiddenBefore.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(
            new System.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{customer.RowVersion}\""));

        // Action
        using var replaceResponse = await Api.ReplaceUserRolesAsync(
            customer.Id,
            [adminRole.Id],
            TestContext.Current.CancellationToken);

        // Assertion
        var updated = await replaceResponse.ReadRequiredJsonAsync<UserResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        updated.Roles.Should().ContainSingle()
            .Which.Code.Should().Be(RoleCodes.Admin);

        replaceResponse.RequireEntityTag();

        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Admin);
        using var allowedAfter = await Api.ListPermissionsAsync(
            TestContext.Current.CancellationToken);

        allowedAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReplaceUserRoles_ForSelf_ReturnsConflict()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var usersResponse = await Api.SearchUsersAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var users = await usersResponse.ReadRequiredJsonAsync<PagedResponse<UserResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var admin = users.Items.Single(
            user => user.ExternalUserId == TestActors.AdminSubject);

        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(
            new System.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{admin.RowVersion}\""));

        // Action
        using var response = await Api.ReplaceUserRolesAsync(
            admin.Id,
            admin.Roles.Select(role => role.Id).ToArray(),
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReplaceUserRoles_WithCustomer_ReturnsForbidden()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.CustomerSubject, RoleCodes.Customer);
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(
            new System.Net.Http.Headers.EntityTagHeaderValue("\"1\""));

        // Action
        using var response = await Api.ReplaceUserRolesAsync(
            userId: 1,
            roleIds: [],
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReplaceUserRoles_WithSystemSubject_ReturnsForbidden()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.SystemId, RoleCodes.System);
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(
            new System.Net.Http.Headers.EntityTagHeaderValue("\"1\""));

        // Action
        using var response = await Api.ReplaceUserRolesAsync(
            userId: 1,
            roleIds: [],
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReplaceUserRoles_WithSystemRole_ReturnsValidationProblem()
    {
        // Arrange
        Api.AuthenticateAs(TestActors.AdminSubject, RoleCodes.Admin);
        using var usersResponse = await Api.SearchUsersAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var users = await usersResponse.ReadRequiredJsonAsync<PagedResponse<UserResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var customer = users.Items.Single(
            user => user.ExternalUserId == TestActors.CustomerSubject);

        using var rolesResponse = await Api.SearchRolesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var roles = await rolesResponse.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        var systemRole = roles.Items.Single(role => role.Code == RoleCodes.System);
        _client.DefaultRequestHeaders.IfMatch.Clear();
        _client.DefaultRequestHeaders.IfMatch.Add(
            new System.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{customer.RowVersion}\""));

        // Action
        using var response = await Api.ReplaceUserRolesAsync(
            customer.Id,
            [systemRole.Id],
            TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))
            .Should().Contain(UserApplicationErrorCodes.SystemRoleAssignmentForbidden);
    }

    private static object CreateWebhookPayload(string userId, string email) => new
    {
        eventName = "User.Created",
        createdAt = DateTimeOffset.UtcNow,
        data = new
        {
            id = userId,
            primaryEmail = email,
            name = "Functional User",
            isSuspended = false,
            createdAt = 0L,
            updatedAt = 0L,
            profile = new { },
            customData = new { },
            identities = new { }
        },
        hookId = "functional-hook"
    };

    private static string ComputeSignature(string body)
    {
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ApiWebApplicationFactory.WebhookSigningKey),
            Encoding.UTF8.GetBytes(body));

        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}