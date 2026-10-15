using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Roles.Common;
using Agent360.UserManagement.UseCase.Roles.Queries.Search;
using Xunit;

namespace Agent360.UserManagement.UseCase.UnitTests.Roles.Queries.Search;

public sealed class SearchRolesQueryHandlerTests
{
    [Fact]
    public async Task Handle_ForwardsTheStructuredRequestAndReturnsSuccessResult()
    {
        // Arrange
        var adapter = A.Fake<IAdvancedSearchQuery<RoleResponse>>();
        var response = new PagedResponse<RoleResponse>([], 2, 10, 0);
        A.CallTo(() => adapter.ExecuteAsync(
                A<AdvancedSearchRequest>._,
                A<CancellationToken>._))
            .Returns(response);
        var handler = new SearchRolesQueryHandler(adapter);
        var request = new AdvancedSearchRequest
        {
            Search =
            [
                new GlobalSearchItem
                {
                    SearchFields = ["Name", "Code"],
                    SearchValue = "admin",
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
            Page = 2,
            Limit = 10
        };

        // Action
        var result = await handler.Handle(
            new SearchRolesQuery(request),
            TestContext.Current.CancellationToken);

        // Assertion
        A.CallTo(() => adapter.ExecuteAsync(
                A<AdvancedSearchRequest>.That.Matches(candidate => ReferenceEquals(candidate, request)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task Handle_MapsAdvancedSearchValidationToFailureResult()
    {
        // Arrange
        var adapter = A.Fake<IAdvancedSearchQuery<RoleResponse>>();
        var exception = new AdvancedSearchValidationException(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["filter.filters[0].searchField"] =
                ["Field 'Secret' is not configured for filtering."],
                ["limit"] = ["Limit cannot exceed 100."]
            });
        A.CallTo(() => adapter.ExecuteAsync(
                A<AdvancedSearchRequest>._,
                A<CancellationToken>._))
            .Throws(exception);
        var handler = new SearchRolesQueryHandler(adapter);

        // Action
        var result = await handler.Handle(
            new SearchRolesQuery(new AdvancedSearchRequest()),
            TestContext.Current.CancellationToken);

        // Assertion
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().OnlyContain(error =>
            error.Type == ErrorType.Validation &&
            error.Code == RoleApplicationErrorCodes.InvalidSearch);
        result.Errors.Should().Contain(error =>
            error.Target == "filter.filters[0].searchField");
        result.Errors.Should().Contain(error => error.Target == "limit");
    }
}