using Agent360.API.Features.Users.Search;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class SearchUsersOpenApiTests
{
    [Fact]
    public void RequestExample_AndMetadata_DescribeTheAllowListedUserSearchContract()
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
        SearchUsersOpenApi.Apply(operation);

        // Assertion
        var example = mediaType.Example.Should().BeAssignableTo<JsonNode>().Which;
        example["search"]![0]!["searchFields"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .Should().Contain(["Email", "DisplayName"]);
        example["filter"]!["filters"]![0]!["operator"]!.GetValue<string>()
            .Should().Be("BW");

        var metadata = operation.Extensions!["x-hmz-advanced-search"]
            .Should().BeOfType<JsonNodeExtension>().Which.Node;
        var metadataObject = metadata.Should().BeOfType<JsonObject>().Which;
        metadataObject["searchableFields"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .Should().Contain(["ExternalUserId", "Email", "FirstName", "LastName"]);
        metadataObject["sortableFields"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .Should().Contain(["Email", "Id"]);
        operation.Description.Should().Contain("Advanced-search configuration");
        operation.Description.Should().Contain("**Defaults and limits**");
        operation.Description.Should().Contain("**Field details**");
        operation.Description.Should().Contain("- **ExternalUserId**");
    }
}