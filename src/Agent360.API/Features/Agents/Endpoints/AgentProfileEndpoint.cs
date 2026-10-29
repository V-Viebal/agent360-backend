using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Endpoints;

/// <summary>
/// Agent profile customization endpoint (custom avatar, color theme, and display name).
/// Stored centrally in Skyer backend and synchronized across devices and sessions.
/// </summary>
public sealed class AgentProfileEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        foreach (var route in new[] { "agents/{agentId}/profile-config", "agents/{agentId}/config/avatar" })
        {
            routeBuilder.MapGet(route, (string agentId, AgentProfileStore store) =>
                {
                    return Results.Ok(store.Get(agentId));
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();

            routeBuilder.MapMethods(route, ["POST", "PUT"], async (string agentId, HttpRequest request, AgentProfileStore store, CancellationToken ct) =>
                {
                    JsonObject? body;
                    try { body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, cancellationToken: ct); }
                    catch (JsonException) { body = null; }

                    if (body is null)
                    {
                        return Results.Problem("Invalid JSON payload", statusCode: StatusCodes.Status400BadRequest);
                    }

                    if (body.ToJsonString().Length > 50000)
                    {
                        return Results.Problem("Payload too large", statusCode: StatusCodes.Status400BadRequest);
                    }

                    var updated = await store.SetAsync(agentId, body);
                    return Results.Ok(updated);
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();

            routeBuilder.MapDelete(route, async (string agentId, AgentProfileStore store) =>
                {
                    await store.ResetAsync(agentId);
                    return Results.Ok(new { success = true, agent_id = agentId });
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();
        }

        // Fleet-wide map retrieval
        routeBuilder.MapGet("agents/profiles-catalog", (AgentProfileStore store) =>
            {
                return Results.Ok(store.GetAll());
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithTags(EndpointTags.Agents)
            .AllowAnonymous();
    }
}

public sealed class AgentProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, JsonObject> _profiles;

    public AgentProfileStore(IOptions<SubAgentOptions> options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.Value.StoragePath) ? options.Value.StoragePath : Path.Combine(env.ContentRootPath, options.Value.StoragePath);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent_profiles_custom.json");
        _profiles = new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, JsonObject>>(File.ReadAllText(_path)) ?? [];
                foreach (var (k, v) in saved) _profiles[k] = v;
            }
            catch (JsonException)
            {
                File.Copy(_path, _path + $".corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
            }
        }
    }

    private static string Key(string agentId) => SubAgentService.NormalizeAgentId(agentId).ToLowerInvariant();

    public JsonObject? Get(string agentId) =>
        _profiles.TryGetValue(Key(agentId), out var v) ? (JsonObject)v.DeepClone() : null;

    public Dictionary<string, JsonObject> GetAll() =>
        _profiles.ToDictionary(k => k.Key, k => (JsonObject)k.Value.DeepClone());

    public async Task<JsonObject> SetAsync(string agentId, JsonObject data)
    {
        var key = Key(agentId);
        var value = (JsonObject)data.DeepClone();
        value["updated_at"] = DateTimeOffset.UtcNow.ToString("O");

        await _lock.WaitAsync();
        try
        {
            _profiles[key] = value;
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_profiles.ToDictionary(k => k.Key, k => k.Value)));
            File.Move(tmp, _path, true);
        }
        finally
        {
            _lock.Release();
        }
        return (JsonObject)value.DeepClone();
    }

    public async Task ResetAsync(string agentId)
    {
        var key = Key(agentId);
        await _lock.WaitAsync();
        try
        {
            if (_profiles.TryRemove(key, out _))
            {
                var tmp = _path + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_profiles.ToDictionary(k => k.Key, k => k.Value)));
                File.Move(tmp, _path, true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
