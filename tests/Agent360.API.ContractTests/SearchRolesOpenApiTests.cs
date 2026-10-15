using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Serialization;
using Agent360.API.Features.Roles.Search;
using Microsoft.OpenApi;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class SearchRolesOpenApiTests
{
    [Fact]
    public void RequestExample_UsesContainsForGlobalSearchAndEqualityForFilter()
    {
        // Arrange
        var mediaType = new OpenApiMediaType();
        var operation = new OpenApiOperation
        {
            RequestBody = new OpenApiRequestBody
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = mediaType
                }
            }
        };

        // Action
        SearchRolesOpenApi.Apply(operation);

        // Assertion
        var example = mediaType.Example
            .Should().BeAssignableTo<System.Text.Json.Nodes.JsonNode>().Which;
        example["search"]![0]!["operator"]!.GetValue<string>().Should().Be("CT");
        example["filter"]!["filters"]![0]!["operator"]!.GetValue<string>()
            .Should().Be("EQ");

        var metadata = operation.Extensions!["x-hmz-advanced-search"]
            .Should().BeOfType<JsonNodeExtension>().Which.Node;
        var metadataObject = metadata.Should()
            .BeOfType<System.Text.Json.Nodes.JsonObject>().Which;
        metadataObject["searchableFields"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .Should().Contain("Code");
        operation.Description.Should().Contain("Advanced-search configuration");
        operation.Description.Should().Contain("**Allowed fields**");
        operation.Description.Should().Contain("- **Searchable:**");
        operation.Description.Should().Contain("- **Filterable:**");
        operation.Description.Should().Contain("- **Sortable:**");
        operation.Description.Should().Contain("**Defaults and limits**");
        operation.Description.Should().Contain("- **Default order**");
        operation.Description.Should().Contain("  - Max page size:");
        operation.Description.Should().Contain("  - Max search groups:");
        operation.Description.Should().Contain("  - Max filters:");
        operation.Description.Should().Contain("  - Max sort fields:");
        operation.Description.Should().Contain("**Field details**");
        operation.Description.Should().Contain("- **Code**");
        operation.Description.Should().Contain("  - Type:");
        operation.Description.Should().Contain("  - Capabilities:");
        operation.Description.Should().Contain("  - Operators:");
    }

    [Fact]
    public void Invalid_sort_direction_uses_the_safe_converter_with_api_json_options()
    {
        // Arrange
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.AddAdvancedSearchConverters();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        Action action = () => JsonSerializer.Deserialize<AdvancedSearchRequest>(
            """
            {
              "orderBy": [
                { "field": "Name", "direction": "sideways" }
              ]
            }
            """,
            options);

        // Action
        var exception = action.Should().Throw<SafeJsonException>().Which;

        // Assertion
        exception.Message.Should().Be(
            "Unknown sort direction 'sideways'. Accepted values: asc or desc.");
    }

    [Theory]
    [InlineData("searchLogic", "sideways", "Unknown search/filter logic 'sideways'. Accepted values: AND or OR.")]
    [InlineData("filter", "sideways", "Unknown search/filter logic 'sideways'. Accepted values: AND or OR.")]
    public void Invalid_search_logic_uses_the_safe_converter(string property, string value, string expectedMessage)
    {
        // Arrange
        var json = property == "filter"
            ? $"{{\"filter\":{{\"logic\":\"{value}\"}}}}"
            : $"{{\"searchLogic\":\"{value}\"}}";

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.AddAdvancedSearchConverters();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        Action action = () => JsonSerializer.Deserialize<AdvancedSearchRequest>(json, options);

        // Action
        var exception = action.Should().Throw<SafeJsonException>().Which;

        // Assertion
        exception.Message.Should().Be(expectedMessage);
    }
}