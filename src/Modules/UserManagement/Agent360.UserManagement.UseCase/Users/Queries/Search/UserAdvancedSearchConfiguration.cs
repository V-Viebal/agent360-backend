using Hmz.Core.SharedKernel.AdvancedSearch;
using Agent360.UserManagement.UseCase.Users.Common;

namespace Agent360.UserManagement.UseCase.Users.Queries.Search;

/// <summary>
/// Defines the client-visible user search contract independently from the
/// entity shape. Role metadata is hydrated after the database page is selected.
/// </summary>
public static class UserAdvancedSearchConfiguration
{
    public static ISearchConfiguration Instance { get; } = new SearchConfiguration(
    [
        new SearchFieldDefinition
        {
            Key = nameof(UserResponse.Id),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            IsUnique = true,
            DataType = SearchDataType.Integer,
            ValueType = typeof(long),
            MaxDigits = 19
        },
        new SearchFieldDefinition
        {
            Key = nameof(UserResponse.ExternalUserId),
            MatchKey = "LogtoUserId",
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 100
        },
        new SearchFieldDefinition
        {
            Key = nameof(UserResponse.Email),
            MatchKey = "Email.Value",
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 256
        },
        new SearchFieldDefinition
        {
            Key = nameof(UserResponse.FirstName),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 100
        },
        new SearchFieldDefinition
        {
            Key = nameof(UserResponse.LastName),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 100
        },
        new SearchFieldDefinition
        {
            Key = nameof(UserResponse.DisplayName),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 200
        }
    ],
    [
        new SearchOrder { Field = nameof(UserResponse.Email), Direction = SortDirection.Asc },
        new SearchOrder { Field = nameof(UserResponse.Id), Direction = SortDirection.Asc }
    ],
    new AdvancedSearchLimits
    {
        MaxPageSize = 100,
        MaxSearchItems = 5,
        MaxFieldsPerSearchItem = 6,
        MaxFilters = 10,
        MaxSortFields = 4
    });
}