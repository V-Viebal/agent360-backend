using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Users.Common;
using Agent360.UserManagement.UseCase.Users.Queries.Search;
using Xunit;

namespace Agent360.UserManagement.UseCase.UnitTests.Users.Queries.Search;

public sealed class SearchUsersQueryHandlerTests
{
    [Fact]
    public async Task Handle_ForwardsTheStructuredRequestAndReturnsSuccess()
    {
        // Arrange
        var adapter = A.Fake<IAdvancedSearchQuery<UserResponse>>();
        var response = new PagedResponse<UserResponse>([], 2, 10, 0);
        A.CallTo(() => adapter.ExecuteAsync(
                A<AdvancedSearchRequest>._,
                A<CancellationToken>._))
            .Returns(response);
        var handler = new SearchUsersQueryHandler(adapter);
        var request = new AdvancedSearchRequest
        {
            Search =
            [
                new GlobalSearchItem
                {
                    SearchFields = ["Email", "DisplayName"],
                    SearchValue = "vincent",
                    Operator = SearchOperator.Contains
                }
            ],
            Page = 2,
            Limit = 10
        };

        // Action
        var result = await handler.Handle(
            new SearchUsersQuery(request),
            TestContext.Current.CancellationToken);

        // Assertion
        A.CallTo(() => adapter.ExecuteAsync(
                A<AdvancedSearchRequest>.That.Matches(candidate =>
                    ReferenceEquals(candidate, request)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task Handle_MapsAdvancedSearchValidationToUserFailure()
    {
        // Arrange
        var adapter = A.Fake<IAdvancedSearchQuery<UserResponse>>();
        A.CallTo(() => adapter.ExecuteAsync(
                A<AdvancedSearchRequest>._,
                A<CancellationToken>._))
            .Throws(new AdvancedSearchValidationException(
                "filter.filters[0].searchField",
                "Field 'Roles' is not configured for filtering."));
        var handler = new SearchUsersQueryHandler(adapter);

        // Action
        var result = await handler.Handle(
            new SearchUsersQuery(new AdvancedSearchRequest()),
            TestContext.Current.CancellationToken);

        // Assertion
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Type.Should().Be(ErrorType.Validation);
        result.Errors[0].Code.Should().Be(UserApplicationErrorCodes.InvalidSearch);
        result.Errors[0].Target.Should().Be("filter.filters[0].searchField");
    }
}