using Hmz.Core.SharedKernel.AdvancedSearch.OpenApi;
using Agent360.UserManagement.UseCase.Users.Queries.Search;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace Agent360.API.Features.Users.Search;

internal static class SearchUsersOpenApi
{
    private static readonly JsonNode RequestExample = JsonNode.Parse(
        """
        {
          "search": [
            {
              "searchFields": ["Email", "FirstName", "LastName", "DisplayName"],
              "searchValue": "System",
              "operator": "CT"
            }
          ],
          "searchLogic": "OR",
          "filter": {
            "logic": "AND",
            "filters": [
              {
                "searchField": "ExternalUserId",
                "searchValues": ["system"],
                "operator": "BW",
                "includeNull": false
              }
            ]
          },
          "page": 1,
          "limit": 20,
          "orderBy": [
            { "field": "Email", "direction": "asc" },
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

        AdvancedSearchOpenApiMetadata.Apply(
            operation,
            UserAdvancedSearchConfiguration.Instance);
    }
}