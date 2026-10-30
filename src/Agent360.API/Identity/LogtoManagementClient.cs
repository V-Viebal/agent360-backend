using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Agent360.API.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Identity;

public sealed record LogtoUserProfile(string Id, string? Email, string? Name, string? AvatarUrl);

/// <summary>
/// Logto Management API access for identity data that access tokens do not carry (profile, user roles).
/// Logto user roles decide platform admin when a local user is provisioned or reconciled; at request time
/// the local PostgreSQL role stays authoritative.
/// </summary>
public sealed class LogtoManagementClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LogtoOptions> options,
    IConfiguration configuration,
    IMemoryCache cache,
    ILogger<LogtoManagementClient> logger)
{
    public const string AdminRoleName = "admin";
    private const string ManagementResource = "https://default.logto.app/api";
    private const string TokenCacheKey = "logto:mgmt:token";

    private string Endpoint
    {
        get
        {
            var authority = (options.Value.Authority ?? "").TrimEnd('/');
            if (authority.EndsWith("/oidc", StringComparison.OrdinalIgnoreCase)) authority = authority[..^5];
            return string.IsNullOrWhiteSpace(authority) ? "https://auth-skyer.hmz.one" : authority;
        }
    }

    // Staging/production env provides Logto__ClientId/Logto__ClientSecret; the fallback keeps hosts that predate it working.
    private string ClientId => FirstNonEmpty(configuration["Logto:ManagementClientId"], options.Value.ClientId, "skyer-m2m-backend");
    private string ClientSecret => FirstNonEmpty(configuration["Logto:ManagementClientSecret"], options.Value.ClientSecret, "7pPJ85w2sM3l7CtgxTVZdGz7WEtvBb9P");

    private static string FirstNonEmpty(params string?[] values) => values.First(v => !string.IsNullOrWhiteSpace(v))!;

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Agent360/1.0");
        return client;
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(TokenCacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)) return cached;

        using var client = CreateClient();
        using var resp = await client.PostAsync($"{Endpoint}/oidc/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["resource"] = ManagementResource,
            ["scope"] = "all",
        }), ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Logto Management API token request failed: {Status}", resp.StatusCode);
            return null;
        }
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
        var token = json?["access_token"]?.ToString();
        var expiresIn = json?["expires_in"]?.GetValue<int>() ?? 3600;
        if (!string.IsNullOrWhiteSpace(token))
        {
            cache.Set(TokenCacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 120)));
        }
        return token;
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        if (token is null) return null;
        var client = CreateClient();
        var req = new HttpRequestMessage(method, $"{Endpoint}/api/{path.TrimStart('/')}") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req, ct);
    }

    public static LogtoUserProfile ParseUser(JsonObject u)
    {
        var google = (u["identities"] as JsonObject)?["google"]?["details"] as JsonObject;
        var email = u["primaryEmail"]?.ToString() ?? google?["email"]?.ToString();
        var name = u["name"]?.ToString() ?? google?["name"]?.ToString() ?? u["username"]?.ToString() ?? email?.Split('@')[0];
        var avatar = u["avatar"]?.ToString() ?? google?["avatar"]?.ToString();
        return new LogtoUserProfile(u["id"]?.ToString() ?? "", email, name, avatar);
    }

    public async Task<LogtoUserProfile?> GetUserAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"users/{Uri.EscapeDataString(userId)}", null, ct);
            if (resp is null || !resp.IsSuccessStatusCode) return null;
            var u = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            return u is null ? null : ParseUser(u);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Logto user lookup failed for {UserId}", userId);
            return null;
        }
    }

    /// <summary>Lists Logto users (all pages).</summary>
    public async Task<IReadOnlyList<JsonObject>?> ListUsersAsync(CancellationToken ct = default)
    {
        var all = new List<JsonObject>();
        for (var page = 1; page <= 50; page++)
        {
            using var resp = await SendAsync(HttpMethod.Get, $"users?page={page}&page_size=100", null, ct);
            if (resp is null || !resp.IsSuccessStatusCode) return page == 1 ? null : all;
            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>(ct);
            var batch = arr?.OfType<JsonObject>().ToList() ?? [];
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    /// <summary>Returns the user's Logto role names, or null when Logto could not be read (callers must not change roles then).</summary>
    public async Task<IReadOnlySet<string>?> GetUserRoleNamesAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"users/{Uri.EscapeDataString(userId)}/roles", null, ct);
            if (resp is null || !resp.IsSuccessStatusCode) return null;
            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>(ct);
            return (arr ?? []).OfType<JsonObject>()
                .Select(r => r["name"]?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Logto role lookup failed for {UserId}", userId);
            return null;
        }
    }

    public async Task<bool?> IsLogtoAdminAsync(string userId, CancellationToken ct = default)
    {
        var roles = await GetUserRoleNamesAsync(userId, ct);
        return roles?.Contains(AdminRoleName);
    }

    private async Task<string?> GetAdminRoleIdAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, "roles?type=User", null, ct);
        if (resp is null || !resp.IsSuccessStatusCode) return null;
        var arr = await resp.Content.ReadFromJsonAsync<JsonArray>(ct);
        return (arr ?? []).OfType<JsonObject>()
            .FirstOrDefault(r => string.Equals(r["name"]?.ToString(), AdminRoleName, StringComparison.OrdinalIgnoreCase))?["id"]?.ToString();
    }

    /// <summary>Grants or revokes the Logto "admin" role so Logto and the local role stay consistent.</summary>
    public async Task<bool> SetAdminAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        try
        {
            var roleId = await GetAdminRoleIdAsync(ct);
            if (roleId is null) return false;
            var current = await IsLogtoAdminAsync(userId, ct);
            if (current is null) return false;
            if (current == isAdmin) return true;

            using var resp = isAdmin
                ? await SendAsync(HttpMethod.Post, $"users/{Uri.EscapeDataString(userId)}/roles", JsonContent.Create(new { roleIds = new[] { roleId } }), ct)
                : await SendAsync(HttpMethod.Delete, $"users/{Uri.EscapeDataString(userId)}/roles/{Uri.EscapeDataString(roleId)}", null, ct);
            return resp is not null && resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Logto admin role update failed for {UserId}", userId);
            return false;
        }
    }
}
