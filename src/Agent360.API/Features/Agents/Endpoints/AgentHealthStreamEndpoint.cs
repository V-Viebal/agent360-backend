using System.Text.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed class AgentHealthStreamEndpoint : IEndpoint
{
    public const string RouteName = "AgentHealthStream";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // GET /api/v1/agents/health/stream - SSE endpoint streaming live semantic health events of all infra agents
        routeBuilder.MapGet(
            "agents/health/stream",
            async (
                HttpContext httpContext,
                IQwenPawClient client,
                CancellationToken ct) =>
            {
                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";

                var responseStream = httpContext.Response.Body;

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var agents = await client.GetAgentsAsync(ct);
                        var payload = new
                        {
                            timestamp = DateTimeOffset.UtcNow.ToString("O"),
                            total_agents = agents.Count,
                            active_agents = agents.Count(a => a.Status == "active"),
                            agents = agents.Select(a => new
                            {
                                id = a.Id,
                                slug = a.Slug,
                                name = a.Name,
                                status = a.Status,
                                node = a.Node,
                                model = a.Model,
                                tools_count = a.Tools.Count
                            })
                        };

                        var json = JsonSerializer.Serialize(payload);
                        var sseEvent = $"event: health_pulse\ndata: {json}\n\n";
                        var bytes = System.Text.Encoding.UTF8.GetBytes(sseEvent);

                        await responseStream.WriteAsync(bytes, ct);
                        await responseStream.FlushAsync(ct);

                        // Heartbeat pulse every 5 seconds
                        await Task.Delay(5000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        var errEvent = $"event: error\ndata: {{\"error\": \"{ex.Message}\"}}\n\n";
                        await responseStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errEvent), ct);
                        await responseStream.FlushAsync(ct);
                        await Task.Delay(5000, ct);
                    }
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("Stream live semantic health status pulses of all agents across infra fleet via SSE")
            .AllowAnonymous();
    }
}
