using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Libraries;

public sealed class SkillLibraryEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Get all skills across agent library / platform
        routeBuilder.MapGet(
            "libraries/skills",
            async Task<IResult> (
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, "api/skills");

                var rawSkills = await httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawSkillDto>>(
                    requestUri,
                    cancellationToken);

                if (rawSkills is null)
                {
                    return TypedResults.Ok(Array.Empty<SkillLibraryItemResponse>());
                }

                var result = rawSkills.Select(s => new SkillLibraryItemResponse(
                    Name: s.Name,
                    Description: s.Description ?? "",
                    Source: s.Source ?? "custom",
                    Emoji: s.Emoji,
                    Enabled: s.Enabled,
                    Channels: s.Channels ?? Array.Empty<string>(),
                    Tags: s.Tags ?? Array.Empty<string>(),
                    LastUpdated: s.LastUpdated
                )).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListSkillsLibrary")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all installed capabilities and skills in the agent library")
            .Produces<IReadOnlyList<SkillLibraryItemResponse>>()
            .AllowAnonymous();

        // 2. Toggle skill enabled/disabled status
        routeBuilder.MapPatch(
            "libraries/skills/{skillName}/toggle",
            async Task<IResult> (
                string skillName,
                [FromBody] ToggleSkillRequest request,
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, $"api/skills/{Uri.EscapeDataString(skillName)}/toggle");

                var response = await httpClient.PostAsJsonAsync(
                    requestUri,
                    new { enabled = request.Enabled },
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new { skill = skillName, enabled = request.Enabled });
                }

                return TypedResults.Problem($"Failed to update skill toggle status on infra runtime: {response.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ToggleSkillLibrary")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Enable or disable a skill in the agent library")
            .AllowAnonymous();
    }
}
