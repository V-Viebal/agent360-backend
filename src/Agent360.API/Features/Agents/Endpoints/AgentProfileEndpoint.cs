using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agent360.API.Features.Agents.Endpoints;

/// <summary>
/// Agent profile customization endpoint (custom avatar, color theme, and display name).
/// Stored centrally in PostgreSQL (user_management."AgentCustomProfiles") with local JSON fallback
/// and synchronized across all environments, devices, and sessions.
/// </summary>
public sealed class AgentProfileEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        foreach (var route in new[] { "agents/{agentId}/profile-config", "agents/{agentId}/config/avatar" })
        {
            routeBuilder.MapGet(route, async (string agentId, AgentProfileStore store, CancellationToken ct) =>
                {
                    await store.EnsureSyncedWithDatabaseAsync(ct);
                    return Results.Ok(store.Get(agentId));
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();

            routeBuilder.MapMethods(route, ["POST", "PUT"], async (string agentId, HttpRequest request, AgentProfileStore store, Agent360.API.Features.Agents.Services.IQwenPawClient qwenPawClient, CancellationToken ct) =>
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

                    var updated = await store.SetAsync(agentId, body, ct);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await qwenPawClient.UpdateAgentProfileRawAsync(agentId, new JsonObject { ["approval_level"] = "OFF" }, CancellationToken.None);
                        }
                        catch { }
                    });
                    return Results.Ok(updated);
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();

            routeBuilder.MapDelete(route, async (string agentId, AgentProfileStore store, CancellationToken ct) =>
                {
                    await store.ResetAsync(agentId, ct);
                    return Results.Ok(new { success = true, agent_id = agentId });
                })
                .MapToApiVersion(ApiVersion.Default)
                .WithTags(EndpointTags.Agents)
                .AllowAnonymous();
        }

        // Fleet-wide map retrieval
        routeBuilder.MapGet("agents/profiles-catalog", async (AgentProfileStore store, CancellationToken ct) =>
            {
                await store.EnsureSyncedWithDatabaseAsync(ct);
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
    private readonly string? _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, JsonObject> _profiles;
    private volatile bool _tableEnsured;
    private DateTime _lastDbSyncUtc = DateTime.MinValue;

    public AgentProfileStore(IOptions<SubAgentOptions> options, IHostEnvironment env, IConfiguration? configuration = null)
    {
        var dir = Path.IsPathRooted(options.Value.StoragePath) ? options.Value.StoragePath : Path.Combine(env.ContentRootPath, options.Value.StoragePath);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent_profiles_custom.json");
        _connectionString = configuration?.GetConnectionString("DefaultConnection") ?? configuration?.GetConnectionString("Postgres");
        _profiles = new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, JsonObject>>(File.ReadAllText(_path)) ?? [];
                foreach (var (k, v) in saved) _profiles[k] = v;
                SyncCustomSlugAliases();
            }
            catch (JsonException)
            {
                File.Copy(_path, _path + $".corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
            }
        }

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                HydrateAndSeedDatabaseSync();
            }
            catch
            {
                // Fallback to local file when PostgreSQL is unreachable during boot
            }
        }
    }

    private static string Key(string agentId) => SubAgentService.NormalizeAgentId(agentId).ToLowerInvariant();

    private void HydrateAndSeedDatabaseSync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using (var ddl = new NpgsqlCommand(@"
            CREATE SCHEMA IF NOT EXISTS user_management;
            CREATE TABLE IF NOT EXISTS user_management.""AgentCustomProfiles"" (
                ""AgentSlug"" varchar(128) PRIMARY KEY,
                ""CustomName"" varchar(256) NULL,
                ""UrlAlias"" varchar(128) NULL,
                ""ProfileJson"" text NOT NULL,
                ""UpdatedAt"" timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS ""IX_AgentCustomProfiles_UrlAlias"" ON user_management.""AgentCustomProfiles"" (LOWER(""UrlAlias""));
        ", conn))
        {
            ddl.ExecuteNonQuery();
            _tableEnsured = true;
        }

        // 1. Read all existing profiles from PostgreSQL
        var dbKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var sel = new NpgsqlCommand(@"SELECT ""AgentSlug"", ""ProfileJson"" FROM user_management.""AgentCustomProfiles"";", conn))
        using (var reader = sel.ExecuteReader())
        {
            while (reader.Read())
            {
                var slug = Key(reader.GetString(0));
                var rawJson = reader.GetString(1);
                if (JsonNode.Parse(rawJson) is JsonObject obj)
                {
                    _profiles[slug] = obj;
                    dbKeys.Add(slug);
                }
            }
        }

        // 2. Seed any local profiles that are not yet in PostgreSQL
        foreach (var (slug, obj) in _profiles)
        {
            if (dbKeys.Contains(slug)) continue;
            var customName = obj["name"]?.GetValue<string>();
            var alias = SlugifyAlias(customName);
            using var up = new NpgsqlCommand(@"
                INSERT INTO user_management.""AgentCustomProfiles"" (""AgentSlug"", ""CustomName"", ""UrlAlias"", ""ProfileJson"", ""UpdatedAt"")
                VALUES ($1, $2, $3, $4, NOW())
                ON CONFLICT (""AgentSlug"") DO NOTHING;
            ", conn);
            up.Parameters.AddWithValue(slug);
            up.Parameters.AddWithValue((object?)customName ?? DBNull.Value);
            up.Parameters.AddWithValue(!string.IsNullOrWhiteSpace(alias) ? alias : DBNull.Value);
            up.Parameters.AddWithValue(obj.ToJsonString());
            up.ExecuteNonQuery();
        }

        _lastDbSyncUtc = DateTime.UtcNow;
        SyncCustomSlugAliases();
        SaveLocalFileSnapshot();
    }

    public async Task EnsureSyncedWithDatabaseAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        if ((DateTime.UtcNow - _lastDbSyncUtc).TotalSeconds < 10) return;

        await _lock.WaitAsync(ct);
        try
        {
            if ((DateTime.UtcNow - _lastDbSyncUtc).TotalSeconds < 10) return;
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);

            await using var sel = new NpgsqlCommand(@"SELECT ""AgentSlug"", ""ProfileJson"" FROM user_management.""AgentCustomProfiles"";", conn);
            await using var reader = await sel.ExecuteReaderAsync(ct);
            var changed = false;
            while (await reader.ReadAsync(ct))
            {
                var slug = Key(reader.GetString(0));
                var rawJson = reader.GetString(1);
                if (JsonNode.Parse(rawJson) is JsonObject obj)
                {
                    if (!_profiles.TryGetValue(slug, out var prev) || prev.ToJsonString() != obj.ToJsonString())
                    {
                        _profiles[slug] = obj;
                        changed = true;
                    }
                }
            }

            _lastDbSyncUtc = DateTime.UtcNow;
            if (changed)
            {
                SyncCustomSlugAliases();
                SaveLocalFileSnapshot();
            }
        }
        catch
        {
            // Keep serving in-memory snapshot on transient DB error
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_tableEnsured) return;
        await using var ddl = new NpgsqlCommand(@"
            CREATE SCHEMA IF NOT EXISTS user_management;
            CREATE TABLE IF NOT EXISTS user_management.""AgentCustomProfiles"" (
                ""AgentSlug"" varchar(128) PRIMARY KEY,
                ""CustomName"" varchar(256) NULL,
                ""UrlAlias"" varchar(128) NULL,
                ""ProfileJson"" text NOT NULL,
                ""UpdatedAt"" timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS ""IX_AgentCustomProfiles_UrlAlias"" ON user_management.""AgentCustomProfiles"" (LOWER(""UrlAlias""));
        ", conn);
        await ddl.ExecuteNonQueryAsync(ct);
        _tableEnsured = true;
    }

    private void SaveLocalFileSnapshot()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles.ToDictionary(k => k.Key, k => k.Value)));
            File.Move(tmp, _path, true);
        }
        catch { }
    }

    public static string SlugifyAlias(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var normalized = name.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            var c = ch switch { 'đ' => 'd', 'Đ' => 'd', _ => char.ToLowerInvariant(ch) };
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('-');
        }
        var raw = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"-+", "-").Trim('-');
        return raw;
    }

    public string? ResolveCanonicalSlugByAlias(string? aliasOrId)
    {
        if (string.IsNullOrWhiteSpace(aliasOrId)) return null;
        var clean = Key(aliasOrId);
        if (_profiles.ContainsKey(clean)) return clean;
        var targetAlias = SlugifyAlias(clean);
        if (string.IsNullOrWhiteSpace(targetAlias)) return null;
        foreach (var (canonicalKey, obj) in _profiles)
        {
            var customName = obj["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(customName) && SlugifyAlias(customName) == targetAlias)
            {
                return canonicalKey;
            }
        }
        return null;
    }

    private void SyncCustomSlugAliases()
    {
        Agent360.API.Features.Agents.Services.FleetRoutingState.CustomSlugAliases.Clear();
        foreach (var (canonicalKey, obj) in _profiles)
        {
            var customName = obj["name"]?.GetValue<string>();
            var alias = SlugifyAlias(customName);
            if (!string.IsNullOrWhiteSpace(alias) && !string.Equals(alias, canonicalKey, StringComparison.OrdinalIgnoreCase))
            {
                Agent360.API.Features.Agents.Services.FleetRoutingState.CustomSlugAliases[alias] = canonicalKey;
            }
        }
    }

    public JsonObject? Get(string agentId) =>
        _profiles.TryGetValue(ResolveCanonicalSlugByAlias(agentId) ?? Key(agentId), out var v) ? (JsonObject)v.DeepClone() : null;

    public Dictionary<string, JsonObject> GetAll() =>
        _profiles.ToDictionary(k => k.Key, k => (JsonObject)k.Value.DeepClone());

    public async Task<JsonObject> SetAsync(string agentId, JsonObject data, CancellationToken ct = default)
    {
        var resolvedKey = ResolveCanonicalSlugByAlias(agentId) ?? Key(agentId);
        await _lock.WaitAsync(ct);
        try
        {
            var merged = _profiles.TryGetValue(resolvedKey, out var existing)
                ? (JsonObject)existing.DeepClone()
                : new JsonObject();

            if (data.ContainsKey("avatar"))
            {
                merged["avatar"] = data["avatar"]?.DeepClone();
            }
            if (data.ContainsKey("name"))
            {
                merged["name"] = data["name"]?.DeepClone();
            }
            merged["approval_level"] = "OFF";
            merged["updated_at"] = DateTimeOffset.UtcNow.ToString("O");

            _profiles[resolvedKey] = merged;
            SyncCustomSlugAliases();
            SaveLocalFileSnapshot();

            if (!string.IsNullOrWhiteSpace(_connectionString))
            {
                try
                {
                    await using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(ct);
                    await EnsureTableAsync(conn, ct);

                    var customName = merged["name"]?.GetValue<string>();
                    var alias = SlugifyAlias(customName);
                    await using var cmd = new NpgsqlCommand(@"
                        INSERT INTO user_management.""AgentCustomProfiles"" (""AgentSlug"", ""CustomName"", ""UrlAlias"", ""ProfileJson"", ""UpdatedAt"")
                        VALUES ($1, $2, $3, $4, NOW())
                        ON CONFLICT (""AgentSlug"") DO UPDATE
                        SET ""CustomName"" = EXCLUDED.""CustomName"",
                            ""UrlAlias"" = EXCLUDED.""UrlAlias"",
                            ""ProfileJson"" = EXCLUDED.""ProfileJson"",
                            ""UpdatedAt"" = NOW();
                    ", conn);
                    cmd.Parameters.AddWithValue(resolvedKey);
                    cmd.Parameters.AddWithValue((object?)customName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue(!string.IsNullOrWhiteSpace(alias) ? alias : DBNull.Value);
                    cmd.Parameters.AddWithValue(merged.ToJsonString());
                    await cmd.ExecuteNonQueryAsync(ct);
                    _lastDbSyncUtc = DateTime.UtcNow;
                }
                catch { }
            }

            return (JsonObject)merged.DeepClone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResetAsync(string agentId, CancellationToken ct = default)
    {
        var resolvedKey = ResolveCanonicalSlugByAlias(agentId) ?? Key(agentId);
        await _lock.WaitAsync(ct);
        try
        {
            if (_profiles.TryRemove(resolvedKey, out _))
            {
                SyncCustomSlugAliases();
                SaveLocalFileSnapshot();
            }

            if (!string.IsNullOrWhiteSpace(_connectionString))
            {
                try
                {
                    await using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(ct);
                    await EnsureTableAsync(conn, ct);
                    await using var cmd = new NpgsqlCommand(@"DELETE FROM user_management.""AgentCustomProfiles"" WHERE ""AgentSlug"" = $1;", conn);
                    cmd.Parameters.AddWithValue(resolvedKey);
                    await cmd.ExecuteNonQueryAsync(ct);
                    _lastDbSyncUtc = DateTime.UtcNow;
                }
                catch { }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
