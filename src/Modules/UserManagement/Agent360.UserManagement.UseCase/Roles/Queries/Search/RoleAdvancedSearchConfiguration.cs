using Hmz.Core.SharedKernel.AdvancedSearch;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Queries.Search;

/// <summary>
/// The endpoint-specific allow list for role search, filters, and ordering.
/// Adding a DTO property does not expose it to Dynamic LINQ unless it is declared here.
/// </summary>
public static class RoleAdvancedSearchConfiguration
{
    public static ISearchConfiguration Instance { get; } = new SearchConfiguration(
    [
        new SearchFieldDefinition
        {
            Key = nameof(RoleResponse.Id),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            IsUnique = true,
            DataType = SearchDataType.Integer,
            ValueType = typeof(long),
            AllowMultipleValues = true,
            MaxDigits = 19
        },
        new SearchFieldDefinition
        {
            Key = nameof(RoleResponse.Name),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 100
        },
        new SearchFieldDefinition
        {
            Key = nameof(RoleResponse.Code),
            IsSearchable = true,
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Text,
            GlobalSearchMode = GlobalSearchMode.TextContains,
            MaxValueLength = 50
        },
        new SearchFieldDefinition
        {
            Key = nameof(RoleResponse.IsSystemRole),
            IsFilterable = true,
            IsSortable = true,
            DataType = SearchDataType.Boolean,
            ValueType = typeof(bool),
            AllowMultipleValues = false
        }
    ],
    [
        new SearchOrder { Field = nameof(RoleResponse.Code), Direction = SortDirection.Asc },
        new SearchOrder { Field = nameof(RoleResponse.Id), Direction = SortDirection.Asc }
    ],
    new AdvancedSearchLimits
    {
        MaxPageSize = 100,
        MaxSearchItems = 5,
        MaxFieldsPerSearchItem = 4,
        MaxFilters = 10,
        MaxSortFields = 4
    });
}