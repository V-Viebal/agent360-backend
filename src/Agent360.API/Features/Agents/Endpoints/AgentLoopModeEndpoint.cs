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
/// Per-agent loop mode (default | goal | mission) is an Agent360 UI preference; QwenPaw has no such route, so the
/// runtime proxy always answered 404. Agent360 owns it: persisted next to the sub-agent data, shared by all devices.
/// </summary>
public sealed class AgentLoopModeEndpoint : IEndpoint
{
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "default", "goal", "mission" };

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        foreach (var route in new[] { "agents/{agentId}/loop-mode", "agents/{agentId}/config/loop-mode" })
        {
            routeBuilder.MapGet(route, (string agentId, AgentLoopModeStore store) => Results.Ok(store.Get(agentId)))
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();

            routeBuilder.MapMethods(route, ["POST", "PUT"], async (string agentId, HttpRequest request, AgentLoopModeStore store) =>
                {
                    JsonObject? body;
                    try { body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body); }
                    catch (JsonException) { body = null; }
                    var mode = body?["mode"]?.GetValue<string>();
                    if (mode is null || !Modes.Contains(mode))
                        return Results.Problem("mode must be default, goal or mission", statusCode: StatusCodes.Status400BadRequest);
                    var options = body?["options"] as JsonObject;
                    if (options is not null && options.ToJsonString().Length > 2000)
                        return Results.Problem("options too large", statusCode: StatusCodes.Status400BadRequest);
                    return Results.Ok(await store.SetAsync(agentId, mode, options));
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();
        }
    }
}

public sealed class AgentLoopModeStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, JsonObject> _modes;

    public AgentLoopModeStore(IOptions<SubAgentOptions> options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.Value.StoragePath) ? options.Value.StoragePath : Path.Combine(env.ContentRootPath, options.Value.StoragePath);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent_loop_modes.json");
        _modes = new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, JsonObject>>(File.ReadAllText(_path)) ?? [];
                foreach (var (k, v) in saved) _modes[k] = v;
            }
            catch (JsonException)
            {
                File.Copy(_path, _path + $".corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
            }
        }
    }

    private static string Key(string agentId) => SubAgentService.NormalizeAgentId(agentId).ToLowerInvariant();

    public JsonObject Get(string agentId) =>
        _modes.TryGetValue(Key(agentId), out var v) ? (JsonObject)v.DeepClone() : new JsonObject { ["mode"] = "default" };

    public async Task<JsonObject> SetAsync(string agentId, string mode, JsonObject? options)
    {
        var value = new JsonObject { ["mode"] = mode, ["updated_at"] = DateTimeOffset.UtcNow.ToString("O") };
        if (options is not null) value["options"] = options.DeepClone();
        await _lock.WaitAsync();
        try
        {
            _modes[Key(agentId)] = value;
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_modes.ToDictionary(k => k.Key, k => k.Value)));
            File.Move(tmp, _path, true);
        }
        finally
        {
            _lock.Release();
        }
        return (JsonObject)value.DeepClone();
    }
}
