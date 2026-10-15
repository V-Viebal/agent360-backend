using Hmz.Core.SharedKernel.AdvancedSearch.OpenApi;
using Agent360.UserManagement.UseCase.Roles.Queries.Search;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace Agent360.API.Features.Roles.Search;

internal static class SearchRolesOpenApi
{
    private static readonly JsonNode RequestExample = JsonNode.Parse(
        """
        {
          "search": [
            {
              "searchFields": ["Id", "Name", "Code"],
              "searchValue": "admin",
              "operator": "CT"
            }
          ],
          "searchLogic": "OR",
          "filter": {
            "logic": "AND",
            "filters": [
              {
                "searchField": "IsSystemRole",
                "searchValues": ["false"],
                "operator": "EQ",
                "includeNull": false
              }
            ]
          },
          "page": 1,
          "limit": 20,
          "orderBy": [
            { "field": "Name", "direction": "asc" },
            { "field": "Id", "direction": "asc" }
          ]
        }
        """)!;

    internal static void Apply(OpenApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.RequestBody?.Content is { } content)
        {
            foreach (var pair in content)
            {
                if (pair.Key.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    pair.Value.Example = RequestExample.DeepClone();
                }
            }
        }

        AdvancedSearchOpenApiMetadata.Apply(operation, RoleAdvancedSearchConfiguration.Instance);
    }
}