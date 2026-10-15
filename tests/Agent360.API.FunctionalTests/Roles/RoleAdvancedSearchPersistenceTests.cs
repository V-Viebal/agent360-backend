using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.UserManagement.UseCase.Roles.Common;
using System.Net;

namespace Agent360.API.FunctionalTests.Roles;

public sealed class RoleAdvancedSearchPersistenceTests(FunctionalTestFixture fixture)
    : FunctionalTestBase(fixture)
{
    [Fact]
    public async Task SearchRoles_ExecutesFiltersAndPaginationAgainstPostgreSql()
    {
        // Arrange
        Api.AuthenticateAs(SystemUsers.SystemId, RoleCodes.System);
        await Api.CreateRoleAndReadAsync("Nocturne Alpha", "NOCTURNE-ALPHA", TestContext.Current.CancellationToken);
        await Api.CreateRoleAndReadAsync("Nocturne Beta", "NOCTURNE-BETA", TestContext.Current.CancellationToken);
        await Api.CreateRoleAndReadAsync("Guest", "GUEST", TestContext.Current.CancellationToken);

        var request = new AdvancedSearchRequest
        {
            Search =
            [
                new GlobalSearchItem
                {
                    SearchFields = ["Name", "Code"],
                    SearchValue = "Nocturne",
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
        using var response = await Api.SearchRolesAsync(request, TestContext.Current.CancellationToken);

        // Assertion
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.ReadRequiredJsonAsync<PagedResponse<RoleResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page!.Items.Should().ContainSingle().Which.Code.Should().Be("NOCTURNE-ALPHA");
        page.TotalCount.Should().Be(2);
    }
}