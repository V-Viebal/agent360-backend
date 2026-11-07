using System.Text.Json.Nodes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>
/// Agent-scoped browser surface. Every call is confined to the agent's own Chromium profile
/// (browser context): pages, screenshots and the screencast never show another agent's tabs.
/// </summary>
public sealed class AgentBrowserCdpEndpoint : IEndpoint
{
    public const string RateLimitPolicy = "agent360-browser";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // Browser identity for the viewer: Selkies URL, CDP endpoint, owned profile, live tabs.
        routeBuilder.MapGet(
            "agents/{agentId}/browser",
            async Task<IResult> (string agentId, HttpContext http, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var info = await directory.GetAsync(agentId, ct);
                return info is null
                    ? TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" })
                    : TypedResults.Ok(info);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Agent's browser service, owned Chromium profile and its live tabs")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/vpn",
            async Task<IResult> (string agentId, HttpContext http, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var info = await directory.GetAsync(agentId, ct);
                if (info is null)
                {
                    return TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" });
                }
                var vpn = info.Vpn ?? directory.GetCachedVpnStatus(info.Service, info.Profile);
                return TypedResults.Ok(new
                {
                    agentId,
                    service = info.Service,
                    profile = info.Profile,
                    enabled = vpn.Enabled,
                    mode = vpn.Mode,
                    proxyUrl = vpn.ProxyUrl,
                    server = vpn.Server,
                    region = vpn.Region,
                    publicIp = vpn.PublicIp
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserVpn")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get per-profile VPN status for the agent's Chromium profile")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapPost(
            "agents/{agentId}/browser/vpn",
            async Task<IResult> (string agentId, [FromBody] BrowserVpnRequest req, HttpContext http, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var vpn = await directory.ConfigureProfileVpnAsync(agentId, req, ct);
                if (vpn is null)
                {
                    return TypedResults.Problem("Failed to configure VPN on agent browser profile", statusCode: StatusCodes.Status502BadGateway);
                }
                return TypedResults.Ok(new
                {
                    ok = true,
                    agentId,
                    enabled = vpn.Enabled,
                    mode = vpn.Mode,
                    proxyUrl = vpn.ProxyUrl,
                    server = vpn.Server,
                    region = vpn.Region,
                    publicIp = vpn.PublicIp
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SetAgentBrowserVpn")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Enable or disable per-profile VPN using the proxy360 IP pool for the agent's Chromium profile")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Value-free health for monitoring (no tabs, URLs or endpoints): stays public while the rest of
        // /browser requires sign-in (SensitiveReadGuardMiddleware).
        routeBuilder.MapGet(
            "agents/{agentId}/browser/health",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var info = await directory.GetAsync(agentId, ct);
                return info is null
                    ? TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" })
                    : TypedResults.Ok(new { agentId, info.Service, info.Profile, info.CdpReachable, info.ProfileWindowOpen });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserHealth")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Whether the agent's browser and profile window are up (no tab data)")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Bring the agent's own profile window to the front of the shared Selkies desktop
        // before the viewer opens it, so the user sees this agent's tabs, not the last agent's.
        routeBuilder.MapPost(
            "agents/{agentId}/browser/focus",
            async Task<IResult> (string agentId, HttpContext http, AgentBrowserDirectory directory, IHttpClientFactory httpFactory, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var (page, cdp, contextId, error) = await PickPageAsync(directory, agentId, null, ct);
                if (page is null)
                {
                    return TypedResults.Conflict(new { message = error });
                }
                var ok = await ActivateAsync(httpFactory, cdp!, page.TargetId, contextId, ct);
                return ok
                    ? TypedResults.Ok(new { agentId, targetId = page.TargetId, url = page.Url })
                    : TypedResults.Problem("Failed to focus the agent's browser window", statusCode: StatusCodes.Status502BadGateway);
            })
            .MapToApiVersion(ApiVersion.Default)
            // Cross-site pages must not raise windows on the shared desktop (CSRF-style abuse).
            .AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                var origin = http.Request.Headers.Origin.FirstOrDefault();
                var cors = http.RequestServices.GetRequiredService<IOptions<CorsOptions>>().Value;
                if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(http, cors.AllowedOrigins))
                {
                    return Results.Problem("Origin not allowed", statusCode: StatusCodes.Status403Forbidden);
                }
                return await next(ctx);
            })
            .WithName("FocusAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Bring the agent's own Chromium profile window to the front of the desktop")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Forward touch/click, wheel scroll, or text input from Mobile Fast View directly into the agent's active CDP tab.
        routeBuilder.MapPost(
            "agents/{agentId}/browser/input",
            async Task<IResult> (
                string agentId,
                [FromBody] BrowserInputRequest req,
                HttpContext http,
                AgentBrowserDirectory directory,
                IMemoryCache cache,
                CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var (page, cdp, contextId, error) = await PickPageAsync(directory, agentId, req.PageId, ct);
                if (page is null)
                {
                    return TypedResults.Conflict(new { message = error });
                }
                var hub = http.RequestServices.GetService<ScreencastHub>();
                var act = (req.Action ?? "click").Trim().ToLowerInvariant();
                var safeReq = act is "new_tab" or "navigate"
                    ? req with { Text = SanitizeNavigationUrl(req.Text) }
                    : req;

                bool ok = false;
                string? createdTargetId = null;
                if (act is "new_tab" or "close_tab" or "activate_tab")
                {
                    // Primary fast path: persistent Browser-level CDP WebSocket (~5ms, zero dependency on page main-thread JS)
                    (ok, createdTargetId) = await directory.ManageTabOverBrowserSocketAsync(cdp!, page.TargetId, contextId, safeReq, ct);
                    if (ok && act == "close_tab" && hub is not null)
                    {
                        var closedTarget = !string.IsNullOrWhiteSpace(safeReq.PageId) ? safeReq.PageId : page.TargetId;
                        hub.RetireTarget(cdp!, closedTarget);
                    }
                    if (!ok && hub is not null)
                    {
                        (ok, createdTargetId) = await hub.TryManageTabOverWarmSessionAsync(cdp!, page.TargetId, contextId, safeReq, ct);
                    }
                    if (ok && act == "new_tab" && !string.IsNullOrEmpty(createdTargetId) && hub is not null)
                    {
                        var isMobNew = safeReq.IsMobile == true || string.Equals(safeReq.DeviceType, "phone", StringComparison.OrdinalIgnoreCase);
                        var initW = safeReq.Width is >= 320 ? safeReq.Width.Value : (isMobNew ? 390 : 1920);
                        var initH = safeReq.Height is >= 240 ? safeReq.Height.Value : (isMobNew ? 720 : 1080);
                        var initNavUrl = isMobNew ? (safeReq.Text ?? "https://www.google.com") : null;
                        hub.PrewarmSession(cdp!, createdTargetId, 1920, 88, initW, initH, isMobNew, initNavUrl);
                    }
                }
                else if (hub is not null)
                {
                    ok = await hub.TryDispatchOverWarmSessionAsync(cdp!, page.TargetId, safeReq, ct);
                }
                if (!ok)
                {
                    (ok, createdTargetId) = await DispatchCdpInputAsync(cdp!, page.TargetId, contextId, safeReq, ct);
                }
                var effectiveTargetId = createdTargetId ?? page.TargetId;
                // Invalidate screenshot cache so immediate follow-up frame reflects the action
                cache.Remove($"agent360:browser:shot:{cdp}:{page.TargetId}");
                if (createdTargetId is not null)
                {
                    cache.Remove($"agent360:browser:shot:{cdp}:{createdTargetId}");
                }

                IReadOnlyList<AgentBrowserTab>? updatedTabs = null;
                if (act == "new_tab" && !string.IsNullOrEmpty(createdTargetId))
                {
                    var targetUrl = safeReq.Text ?? "https://www.google.com";
                    var targetTitle = targetUrl.Contains("google.com", StringComparison.OrdinalIgnoreCase) ? "Google" : targetUrl;
                    var createdTab = new AgentBrowserTab(createdTargetId, targetTitle, targetUrl, false);
                    directory.RecordTabCreated(createdTargetId, createdTab);
                    updatedTabs = await directory.MutateCachedTabsAsync(agentId, list =>
                    {
                        if (list.All(t => t.TargetId != createdTargetId))
                        {
                            list.Add(createdTab);
                        }
                        return list;
                    }, ct) ?? [createdTab];
                }
                else if (act == "close_tab")
                {
                    var closedId = !string.IsNullOrWhiteSpace(safeReq.PageId) ? safeReq.PageId : page.TargetId;
                    directory.RecordTabClosed(closedId);
                    updatedTabs = await directory.MutateCachedTabsAsync(agentId, list =>
                        list.Where(t => t.TargetId != closedId).ToList(), ct);
                }
                else if (act == "navigate" && !string.IsNullOrEmpty(safeReq.Text))
                {
                    var navUrl = safeReq.Text!;
                    updatedTabs = await directory.MutateCachedTabsAsync(agentId, list =>
                        list.Select(t => t.TargetId == page.TargetId ? t with { Url = navUrl, Title = navUrl } : t).ToList(), ct);
                }
                return ok
                    ? TypedResults.Ok(new { agentId, targetId = effectiveTargetId, createdTargetId, action = req.Action, tabs = updatedTabs })
                    : TypedResults.Problem("Failed to dispatch input to agent browser tab", statusCode: StatusCodes.Status502BadGateway);
            })
            .MapToApiVersion(ApiVersion.Default)
            .AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                var origin = http.Request.Headers.Origin.FirstOrDefault();
                var cors = http.RequestServices.GetRequiredService<IOptions<CorsOptions>>().Value;
                if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(http, cors.AllowedOrigins))
                {
                    return Results.Problem("Origin not allowed", statusCode: StatusCodes.Status403Forbidden);
                }
                return await next(ctx);
            })
            .WithName("DispatchAgentBrowserInput")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Dispatch touch click, scroll, or key input directly to the agent's active browser tab via CDP")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/pages",
            async Task<IResult> (string agentId, HttpContext http, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var info = await directory.GetAsync(agentId, ct);
                if (info is null)
                {
                    return TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" });
                }
                return TypedResults.Ok(info.Tabs.Where(t => !t.Anchor)
                    .Select(t => new BrowserPageSummary(t.TargetId, t.Title, t.Url, "page", null)).ToList());
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserPages")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List the agent's own browser tabs (its Chromium profile only)")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/history",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, HttpContext http, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.Ok(Array.Empty<object>());
                }
                var entries = await GetNavigationHistoryAsync(cdp!, page.TargetId, ct);
                return TypedResults.Ok(entries);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserHistory")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get navigation history entries of the agent's browser tab")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/suggest",
            async Task<IResult> (string agentId, [FromQuery] string? q, IHttpClientFactory httpFactory, CancellationToken ct) =>
            {
                var query = (q ?? "").Trim();
                if (string.IsNullOrEmpty(query) || query.Length > 160)
                {
                    return TypedResults.Ok(Array.Empty<string>());
                }
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(1200));
                    var client = httpFactory.CreateClient();
                    var url = $"https://suggestqueries.google.com/complete/search?client=firefox&hl=vi&q={Uri.EscapeDataString(query)}";
                    var json = await client.GetStringAsync(url, cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 1)
                    {
                        var items = doc.RootElement[1];
                        if (items.ValueKind == JsonValueKind.Array)
                        {
                            var results = items.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString() ?? "")
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Take(6)
                                .ToList();
                            return TypedResults.Ok(results);
                        }
                    }
                }
                catch { }
                return TypedResults.Ok(Array.Empty<string>());
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserSuggestions")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get search autocomplete suggestions for the agent browser omnibox")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/screenshot",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, HttpContext http, AgentBrowserDirectory directory, AgentBrowserGate gate, IMemoryCache cache, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.NotFound(new { message = error });
                }
                var data = await CachedScreenshotAsync(gate, cache, cdp!, page.TargetId);
                return data is null
                    ? TypedResults.Problem("Failed to capture screenshot from CDP target.", statusCode: StatusCodes.Status502BadGateway)
                    : TypedResults.Ok(new BrowserScreenshotResponse(agentId, page.TargetId, "jpeg", data, DateTimeOffset.UtcNow));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserScreenshot")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Capture a screenshot of the agent's own browser tab")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/image.jpg",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, AgentBrowserDirectory directory, AgentBrowserGate gate, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
            {
                if (http.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, http, ct) is { } denied)
                {
                    return denied;
                }
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.NotFound(new { message = error });
                }
                http.Response.Headers.CacheControl = "no-store";
                var key = $"agent360:browser-shot:{page.TargetId}";
                var data = http.RequestServices.GetService<ScreencastHub>()?.LatestFrame(cdp!, page.TargetId)
                    ?? await CachedScreenshotAsync(gate, cache, cdp!, page.TargetId);
                if (data is not null)
                {
                    cache.Set(key, data, TimeSpan.FromMinutes(5));
                }
                else if (cache.TryGetValue(key, out string? last) && last is not null)
                {
                    // A busy or background-throttled tab is common; a slightly stale frame beats a broken preview.
                    http.Response.Headers["X-Agent360-Stale"] = "1";
                    data = last;
                }
                else if (http.RequestServices.GetService<ScreencastHub>()?.LatestFrame(cdp!, page.TargetId) is { } live)
                {
                    http.Response.Headers["X-Agent360-Stale"] = "1";
                    data = live;
                }
                return data is null
                    ? TypedResults.Problem("Failed to capture screenshot", statusCode: StatusCodes.Status503ServiceUnavailable)
                    : TypedResults.File(Convert.FromBase64String(data), "image/jpeg");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserRawImage")
            .WithTags(EndpointTags.Agents)
            .WithSummary("JPEG of the agent's own browser tab")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Live screencast of the agent's tab (inspection only; interactive control is the Selkies viewer).
        routeBuilder.Map(
            "agents/{agentId}/browser/stream",
            async (string agentId, HttpContext context, AgentBrowserDirectory directory, AgentBrowserGate gate, IOptions<CorsOptions> cors) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection expected.");
                    return;
                }
                // Browsers always send Origin on a WebSocket handshake; only Agent360's own UI may watch.
                if (!IsAllowedOrigin(context, cors.Value.AllowedOrigins))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                var ct = context.RequestAborted;
                if (context.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, context, ct) is not null)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, context.Request.Query["pageId"], ct);
                if (page is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { message = error }, ct);
                    return;
                }
                var info = await directory.GetAsync(agentId, ct);
                var slots = gate.StreamSlots(info!.Instance);
                if (!await slots.WaitAsync(0, ct))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.Response.WriteAsJsonAsync(new { message = "Too many live viewers on this browser; use the preview." }, ct);
                    return;
                }
                try
                {
                    using var clientWs = await context.WebSockets.AcceptWebSocketAsync();
                    // Fixed size/quality buckets so concurrent viewers of one tab share a single CDP screencast.
                    var maxWidth = int.TryParse(context.Request.Query["maxWidth"], out var w) && w <= 800 ? 1280 : 1920;
                    var initVw = int.TryParse(context.Request.Query["vw"], out var parsedVw) ? parsedVw : 0;
                    var initVh = int.TryParse(context.Request.Query["vh"], out var parsedVh) ? parsedVh : 0;
                    var isMobileDevice = context.Request.Query["mobile"] == "1"
                        || string.Equals(context.Request.Query["device"], "phone", StringComparison.OrdinalIgnoreCase);
                    var quality = int.TryParse(context.Request.Query["q"], out var parsedQ) ? Math.Clamp(parsedQ, 52, 92) : 88;
                    var hub = context.RequestServices.GetRequiredService<ScreencastHub>();
                    // Only use warm LatestFrame from an already-resized session; never push an unscaled 1440x813 CachedScreenshotAsync seed when vw/vh are requested!
                    var seed = hub.LatestFrame(cdp!, page.TargetId, initVw, initVh, isMobileDevice);
                    if (seed is null && (initVw < 320 || initVh < 240))
                    {
                        seed = await CachedScreenshotAsync(gate, context.RequestServices.GetRequiredService<IMemoryCache>(), cdp!, page.TargetId);
                    }
                    // Do not call WarmTabsInBackground on inactive tabs: background Session startup calls Page.bringToFront + Browser.setWindowBounds across the same window and resets Emulation.setDeviceMetricsOverride on the active tab!
                    var useBinary = context.Request.Query["bin"] == "1";
                    await hub.WatchAsync(cdp!, page.TargetId, maxWidth, quality, clientWs, seed, ct, initVw, initVh, useBinary, isMobileDevice);
                }
                finally
                {
                    slots.Release();
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("StreamAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .ExcludeFromDescription()
            .RequireRateLimiting(RateLimitPolicy);
    }

    /// <summary>The tab worth showing by default: a real web page first, never a chrome:// internal page
    /// (chrome://newtab renders out-of-process and Page.captureScreenshot hangs on it), else the profile anchor.</summary>
    internal static AgentBrowserTab? DefaultTab(IReadOnlyList<AgentBrowserTab> tabs) =>
        tabs.LastOrDefault(t => !t.Anchor && IsCapturable(t.Url))
        ?? tabs.FirstOrDefault(t => !t.Anchor && IsCapturable(t.Url))
        ?? tabs.FirstOrDefault(t => t.Anchor)
        ?? tabs.LastOrDefault(t => IsCapturable(t.Url));

    internal static string SanitizeNavigationUrl(string? raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return "https://www.google.com";
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("chrome-extension:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.google.com";
        }
        if (!trimmed.Contains("://") && !trimmed.StartsWith("about:"))
        {
            trimmed = trimmed.Contains(".") && !trimmed.Contains(" ")
                ? $"https://{trimmed}"
                : $"https://www.google.com/search?q={Uri.EscapeDataString(trimmed)}";
        }
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var u))
        {
            if (u.Host.Equals("169.254.169.254", StringComparison.OrdinalIgnoreCase) ||
                u.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            {
                return "https://www.google.com";
            }
        }
        return trimmed;
    }

    internal static bool IsCapturable(string url) =>
        !url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("chrome-untrusted://", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowedOrigin(HttpContext context, IReadOnlyCollection<string> allowed)
    {
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }
        if (allowed.Any(a => string.Equals(a.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        // Same-origin (UI and API behind one host via the frontend nginx /api proxy)
        // or local development loopback (e.g. Vite dev server on localhost:3000 or 127.0.0.1:3000).
        if (Uri.TryCreate(origin, UriKind.Absolute, out var o))
        {
            if (string.Equals(o.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (o.IsLoopback || o.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>One CDP capture per target per second, shared by every concurrent viewer.</summary>
    private static async Task<string?> CachedScreenshotAsync(AgentBrowserGate gate, IMemoryCache cache, string cdpUrl, string targetId)
    {
        var key = $"agent360:browser:shot:{cdpUrl}:{targetId}";
        if (cache.TryGetValue(key, out string? hit) && hit is not null)
        {
            return hit;
        }
        var data = await gate.Coalesce(key, async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                return await CaptureScreenshotAsync(cdpUrl, targetId, cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or HttpRequestException)
            {
                return null;
            }
        });
        if (data is not null)
        {
            cache.Set(key, data, TimeSpan.FromSeconds(1));
        }
        return data;
    }

    internal static async Task<bool> FocusAgentWindowAsync(AgentBrowserDirectory directory, IHttpClientFactory httpFactory, string agentId, CancellationToken ct)
    {
        var (page, cdp, contextId, _) = await PickPageAsync(directory, agentId, null, ct);
        if (page is null || string.IsNullOrEmpty(cdp)) return false;
        return await ActivateAsync(httpFactory, cdp, page.TargetId, contextId, ct);
    }

    private static async Task<(AgentBrowserTab? Page, string? Cdp, string? ContextId, string? Error)> PickPageAsync(
        AgentBrowserDirectory directory, string agentId, string? pageId, CancellationToken ct)
    {
        var info = await directory.GetAsync(agentId, ct);
        if (info is null)
        {
            return (null, null, null, $"Agent '{agentId}' has no mapped browser profile");
        }
        if (!info.CdpReachable)
        {
            return (null, null, null, $"Browser {info.Service} is not reachable");
        }
        if (!info.ProfileWindowOpen)
        {
            return (null, null, null, $"Profile '{info.Profile}' window is not open yet");
        }
        // Only targets inside the agent's own browser context are eligible; a foreign pageId is rejected.
        var page = string.IsNullOrEmpty(pageId)
            ? DefaultTab(info.Tabs)
            : (info.Tabs.FirstOrDefault(t => t.TargetId == pageId) ?? DefaultTab(info.Tabs));
        if (page is null)
        {
            info = await directory.GetAsync(agentId, ct, forceRefresh: true);
            if (info is not null && info.CdpReachable && info.ProfileWindowOpen)
            {
                page = string.IsNullOrEmpty(pageId)
                    ? DefaultTab(info.Tabs)
                    : (info.Tabs.FirstOrDefault(t => t.TargetId == pageId) ?? DefaultTab(info.Tabs));
            }
        }
        return page is null ? (null, null, null, $"No tab of agent '{agentId}' matches") : (page, info!.CdpUrl, info.BrowserContextId, null);
    }

    private static async Task<bool> ActivateAsync(IHttpClientFactory httpFactory, string cdpUrl, string targetId, string? targetContextId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var http = httpFactory.CreateClient(AgentBrowserDirectory.HttpClientName);

        // Under Wayland / labwc compositor, pure /json/activate only switches the active tab internally
        // but labwc denies unseeded window activation ("Denying focus request, seat wasn't supplied").
        // To guarantee the agent's profile window is brought on top of the shared desktop:
        // 1. Connect to browser WebSocket debugger.
        // 2. Find target windowIds (all windows matching the profile context or targetId).
        // 3. Minimize other profile windows so labwc un-maps them.
        // 4. Restore target profile windows (normal -> maximized) to place them on top of the window stack.
        // 5. Activate target tab. Fallback to /json/activate if browser WebSocket is unreachable.
        try
        {
            var version = await http.GetFromJsonAsync<JsonObject>($"{cdpUrl.TrimEnd('/')}/json/version", cts.Token);
            var browserWs = version?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(browserWs))
            {
                var wsUri = new UriBuilder(browserWs) { Host = new Uri(cdpUrl).Host, Port = new Uri(cdpUrl).Port }.Uri;
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(wsUri, cts.Token);

                int reqId = 100;
                await SendAsync(ws, new { id = ++reqId, method = "Target.getTargets" }, cts.Token);
                var targetsBytes = await ReceiveByIdAsync(ws, reqId, cts.Token);
                if (targetsBytes is not null)
                {
                    using var doc = JsonDocument.Parse(targetsBytes);
                    var pages = doc.RootElement.GetProperty("result").GetProperty("targetInfos").EnumerateArray()
                        .Where(t => t.TryGetProperty("type", out var type) && type.GetString() == "page")
                        .Select(t => (
                            TargetId: t.GetProperty("targetId").GetString() ?? "",
                            ContextId: t.TryGetProperty("browserContextId", out var c) ? c.GetString() : null))
                        .ToList();

                    var targetWids = new HashSet<int>();
                    var otherWids = new HashSet<int>();
                    foreach (var p in pages)
                    {
                        var callId = ++reqId;
                        await SendAsync(ws, new { id = callId, method = "Browser.getWindowForTarget", @params = new { targetId = p.TargetId } }, cts.Token);
                        var winBytes = await ReceiveByIdAsync(ws, callId, cts.Token);
                        if (winBytes is not null)
                        {
                            using var winDoc = JsonDocument.Parse(winBytes);
                            if (winDoc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("windowId", out var widProp))
                            {
                                var wid = widProp.GetInt32();
                                bool isTarget = p.TargetId == targetId
                                    || (!string.IsNullOrEmpty(targetContextId) && p.ContextId == targetContextId);
                                if (isTarget)
                                {
                                    targetWids.Add(wid);
                                }
                                else
                                {
                                    otherWids.Add(wid);
                                }
                            }
                        }
                    }

                    if (targetWids.Count > 0)
                    {
                        otherWids.ExceptWith(targetWids);
                        foreach (var wid in otherWids)
                        {
                            var hideId = ++reqId;
                            await SendAsync(ws, new { id = hideId, method = "Browser.setWindowBounds", @params = new { windowId = wid, bounds = new { windowState = "minimized" } } }, cts.Token);
                            await ReceiveByIdAsync(ws, hideId, cts.Token);
                        }

                        // Auto-dismiss Lens overlay or hanging lens UI targets and profile-picker if present
                        var extraTargets = doc.RootElement.GetProperty("result").GetProperty("targetInfos").EnumerateArray()
                            .Where(t => {
                                var tid = t.GetProperty("targetId").GetString() ?? "";
                                var turl = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                                return tid != targetId && (tid.Contains("lens", StringComparison.OrdinalIgnoreCase) || turl.Contains("profile-picker", StringComparison.OrdinalIgnoreCase));
                            })
                            .Select(t => t.GetProperty("targetId").GetString() ?? "")
                            .ToList();
                        foreach (var etId in extraTargets)
                        {
                            var closeId = ++reqId;
                            await SendAsync(ws, new { id = closeId, method = "Target.closeTarget", @params = new { targetId = etId } }, cts.Token);
                            await ReceiveByIdAsync(ws, closeId, cts.Token);
                        }

                        // Never toggle normal -> maximized on an already open window, because Chromium resets Emulation.setDeviceMetricsOverride on every X11/Wayland window configure event!
                    }
                }

                var actId = ++reqId;
                await SendAsync(ws, new { id = actId, method = "Target.activateTarget", @params = new { targetId } }, cts.Token);
                await ReceiveByIdAsync(ws, actId, cts.Token);
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                return true;
            }
        }
        catch
        {
            // Fallback to HTTP /json/activate
        }

        using var res = await http.GetAsync($"{cdpUrl.TrimEnd('/')}/json/activate/{Uri.EscapeDataString(targetId)}", cts.Token);
        return res.IsSuccessStatusCode;
    }

    private static async Task<(bool Ok, string? CreatedTargetId)> DispatchCdpInputAsync(string cdpUrl, string targetId, string? browserContextId, BrowserInputRequest req, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);

            // Ensure target page has active lifecycle and focus emulation so mouse/wheel/keyboard events take effect immediately
            await SendAsync(ws, new { id = 10, method = "Emulation.setFocusEmulationEnabled", @params = new { enabled = true } }, cts.Token);
            await SendAsync(ws, new { id = 11, method = "Page.setWebLifecycleState", @params = new { state = "active" } }, cts.Token);

            int id = 200;
            string? createdTargetId = null;
            var action = (req.Action ?? "click").Trim().ToLowerInvariant();
            double vw = 1440, vh = 813;

            if (action is "click" or "scroll" or "mousedown" or "mousemove_drag" or "mouseup")
            {
                var layoutId = ++id;
                await SendAsync(ws, new { id = layoutId, method = "Page.getLayoutMetrics" }, cts.Token);
                var layoutBytes = await ReceiveByIdAsync(ws, layoutId, cts.Token);
                if (layoutBytes is not null)
                {
                    using var doc = JsonDocument.Parse(layoutBytes);
                    if (doc.RootElement.TryGetProperty("result", out var res) &&
                        res.TryGetProperty("cssVisualViewport", out var vp))
                    {
                        if (vp.TryGetProperty("clientWidth", out var cw) && cw.GetDouble() > 0) vw = cw.GetDouble();
                        if (vp.TryGetProperty("clientHeight", out var ch) && ch.GetDouble() > 0) vh = ch.GetDouble();
                    }
                }
            }

            var x = Math.Round(Math.Clamp(req.XRatio ?? 0.5, 0.0, 1.0) * vw);
            var y = Math.Round(Math.Clamp(req.YRatio ?? 0.5, 0.0, 1.0) * vh);

            if (action == "click")
            {
                // Pipeline mouseMoved + mousePressed + mouseReleased and await only the final ack for 3x faster click response
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, cts.Token);
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 } }, cts.Token);
                var upId = ++id;
                await SendAsync(ws, new { id = upId, method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 } }, cts.Token);
                await ReceiveByIdAsync(ws, upId, cts.Token);
            }
            else if (action == "dblclick")
            {
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, cts.Token);
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 2 } }, cts.Token);
                var upId = ++id;
                await SendAsync(ws, new { id = upId, method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 2 } }, cts.Token);
                await ReceiveByIdAsync(ws, upId, cts.Token);
            }
            else if (action == "rightclick")
            {
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, cts.Token);
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "right", buttons = 2, clickCount = 1 } }, cts.Token);
                var upId = ++id;
                await SendAsync(ws, new { id = upId, method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "right", buttons = 0, clickCount = 1 } }, cts.Token);
                await ReceiveByIdAsync(ws, upId, cts.Token);
            }
            else if (action == "mousedown")
            {
                await SendAsync(ws, new { id = ++id, method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none", buttons = 0 } }, cts.Token);
                var downId = ++id;
                await SendAsync(ws, new { id = downId, method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 } }, cts.Token);
                await ReceiveByIdAsync(ws, downId, cts.Token);
            }
            else if (action == "mousemove_drag")
            {
                var dragId = ++id;
                await SendAsync(ws, new { id = dragId, method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "left", buttons = 1 } }, cts.Token);
                await ReceiveByIdAsync(ws, dragId, cts.Token);
            }
            else if (action == "mouseup")
            {
                var relId = ++id;
                await SendAsync(ws, new { id = relId, method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 } }, cts.Token);
                await ReceiveByIdAsync(ws, relId, cts.Token);
            }
            else if (action == "scroll_to")
            {
                var ratio = Math.Clamp(req.YRatio ?? 0.0, 0.0, 1.0);
                var ratioStr = ratio.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
                var jsScroll = $$"""(() => { try { const docEl = document.scrollingElement || document.documentElement || document.body; if (docEl && docEl.scrollHeight > docEl.clientHeight) { window.scrollTo({ top: Math.round((docEl.scrollHeight - docEl.clientHeight) * {{ratioStr}}), behavior: 'instant' }); } } catch {} })()""";
                var scId = ++id;
                await SendAsync(ws, new { id = scId, method = "Runtime.evaluate", @params = new { expression = jsScroll } }, cts.Token);
                await ReceiveByIdAsync(ws, scId, cts.Token);
            }
            else if (action == "scroll")
            {
                var wheelId = ++id;
                var dx = Math.Clamp(req.DeltaX ?? 0, -2400, 2400);
                var dy = Math.Clamp(req.DeltaY ?? 0, -2400, 2400);
                var ix = (int)x;
                var iy = (int)y;
                var dxStr = dx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                var dyStr = dy.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                var jsScrollBy = $$"""(() => { try { const dx = {{dxStr}}, dy = {{dyStr}}; let el = document.elementFromPoint({{ix}}, {{iy}}); let scrolled = false; while (el && el !== document.body && el !== document.documentElement) { const cs = window.getComputedStyle(el); const canY = Math.abs(dy) > 0.05 && (cs.overflowY === 'auto' || cs.overflowY === 'scroll' || cs.overflowY === 'overlay') && el.scrollHeight > el.clientHeight + 1; const canX = Math.abs(dx) > 0.05 && (cs.overflowX === 'auto' || cs.overflowX === 'scroll' || cs.overflowX === 'overlay') && el.scrollWidth > el.clientWidth + 1; if (canY || canX) { const prevTop = el.scrollTop, prevLeft = el.scrollLeft; if (canY) el.scrollTop += dy; if (canX) el.scrollLeft += dx; if (Math.abs(el.scrollTop - prevTop) > 0.2 || Math.abs(el.scrollLeft - prevLeft) > 0.2) { scrolled = true; break; } } el = el.parentElement; } if (!scrolled) { window.scrollBy({ left: dx, top: dy, behavior: 'instant' }); } } catch {} })()""";
                await SendAsync(ws, new { id = wheelId, method = "Runtime.evaluate", @params = new { expression = jsScrollBy } }, cts.Token);
                await ReceiveByIdAsync(ws, wheelId, cts.Token);
            }
            else if (action == "ime_commit")
            {
                var bsCount = Math.Clamp(req.Backspaces ?? 0, 0, 64);
                int lastAckId = 0;
                for (int i = 0; i < bsCount; i++)
                {
                    await SendAsync(ws, new
                    {
                        id = ++id,
                        method = "Input.dispatchKeyEvent",
                        @params = new
                        {
                            type = "rawKeyDown",
                            key = "Backspace",
                            code = "Backspace",
                            windowsVirtualKeyCode = 8,
                            nativeVirtualKeyCode = 8,
                            commands = new[] { "DeleteBackward" }
                        }
                    }, cts.Token);
                    lastAckId = ++id;
                    await SendAsync(ws, new
                    {
                        id = lastAckId,
                        method = "Input.dispatchKeyEvent",
                        @params = new
                        {
                            type = "keyUp",
                            key = "Backspace",
                            code = "Backspace",
                            windowsVirtualKeyCode = 8,
                            nativeVirtualKeyCode = 8
                        }
                    }, cts.Token);
                }
                if (!string.IsNullOrEmpty(req.Text))
                {
                    var normText = req.Text.Normalize(System.Text.NormalizationForm.FormC);
                    lastAckId = ++id;
                    await SendAsync(ws, new { id = lastAckId, method = "Input.insertText", @params = new { text = normText } }, cts.Token);
                }
                if (lastAckId > 0)
                {
                    await ReceiveByIdAsync(ws, lastAckId, cts.Token);
                }
            }
            else if (action == "type" && !string.IsNullOrEmpty(req.Text))
            {
                var normText = req.Text.Normalize(System.Text.NormalizationForm.FormC);
                var txtId = ++id;
                await SendAsync(ws, new { id = txtId, method = "Input.insertText", @params = new { text = normText } }, cts.Token);
                await ReceiveByIdAsync(ws, txtId, cts.Token);
            }
            else if (action == "key" && !string.IsNullOrEmpty(req.Key))
            {
                var k = req.Key;
                var mods = req.Modifiers ?? 0;
                bool hasCtrlOrMeta = (mods & 2) != 0 || (mods & 4) != 0;
                bool hasShift = (mods & 8) != 0;
                bool isBackspace = k.Equals("Backspace", StringComparison.OrdinalIgnoreCase);
                bool isDelete = k.Equals("Delete", StringComparison.OrdinalIgnoreCase);
                bool isEnter = k.Equals("Enter", StringComparison.OrdinalIgnoreCase);
                int keyCode = isEnter ? 13
                    : isBackspace ? 8
                    : k.Equals("Escape", StringComparison.OrdinalIgnoreCase) ? 27
                    : k.Equals("Tab", StringComparison.OrdinalIgnoreCase) ? 9
                    : k.Equals("ArrowLeft", StringComparison.OrdinalIgnoreCase) ? 37
                    : k.Equals("ArrowUp", StringComparison.OrdinalIgnoreCase) ? 38
                    : k.Equals("ArrowRight", StringComparison.OrdinalIgnoreCase) ? 39
                    : k.Equals("ArrowDown", StringComparison.OrdinalIgnoreCase) ? 40
                    : isDelete ? 46
                    : k.Equals("Home", StringComparison.OrdinalIgnoreCase) ? 36
                    : k.Equals("End", StringComparison.OrdinalIgnoreCase) ? 35
                    : k.Equals("PageUp", StringComparison.OrdinalIgnoreCase) ? 33
                    : k.Equals("PageDown", StringComparison.OrdinalIgnoreCase) ? 34
                    : k.Equals(" ", StringComparison.Ordinal) || k.Equals("Space", StringComparison.OrdinalIgnoreCase) ? 32
                    : (k.Length == 1 ? (int)char.ToUpperInvariant(k[0]) : 0);
                var codeStr = k.Length == 1 && char.IsLetter(k[0]) ? $"Key{char.ToUpperInvariant(k[0])}"
                    : k.Length == 1 && char.IsDigit(k[0]) ? $"Digit{k[0]}"
                    : (k == " " ? "Space" : k);
                bool isPrintable = k.Length == 1 && !char.IsControl(k[0]) && !hasCtrlOrMeta;
                string? keyText = isEnter ? "\r" : (isPrintable ? k : null);
                string downType = (isPrintable || isEnter) ? "keyDown" : "rawKeyDown";
                string[]? commands = null;
                if (isBackspace)
                {
                    commands = new[] { hasCtrlOrMeta ? "DeleteWordBackward" : "DeleteBackward" };
                }
                else if (isDelete)
                {
                    commands = new[] { hasCtrlOrMeta ? "DeleteWordForward" : "DeleteForward" };
                }
                else if (k.Equals("ArrowLeft", StringComparison.OrdinalIgnoreCase))
                {
                    commands = new[] { hasCtrlOrMeta ? (hasShift ? "MoveWordLeftAndModifySelection" : "MoveWordLeft") : (hasShift ? "MoveLeftAndModifySelection" : "MoveLeft") };
                }
                else if (k.Equals("ArrowRight", StringComparison.OrdinalIgnoreCase))
                {
                    commands = new[] { hasCtrlOrMeta ? (hasShift ? "MoveWordRightAndModifySelection" : "MoveWordRight") : (hasShift ? "MoveRightAndModifySelection" : "MoveRight") };
                }
                else if (k.Equals("ArrowUp", StringComparison.OrdinalIgnoreCase))
                {
                    commands = new[] { hasShift ? "MoveUpAndModifySelection" : "MoveUp" };
                }
                else if (k.Equals("ArrowDown", StringComparison.OrdinalIgnoreCase))
                {
                    commands = new[] { hasShift ? "MoveDownAndModifySelection" : "MoveDown" };
                }
                else if (k.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    commands = new[] { hasShift ? "MoveToBeginningOfLineAndModifySelection" : "MoveToBeginningOfLine" };
                }
                else if (k.Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    commands = new[] { hasShift ? "MoveToEndOfLineAndModifySelection" : "MoveToEndOfLine" };
                }
                else if (hasCtrlOrMeta && k.Length == 1)
                {
                    var lower = char.ToLowerInvariant(k[0]);
                    commands = lower switch
                    {
                        'a' => new[] { "SelectAll" },
                        'c' => new[] { "Copy" },
                        'x' => new[] { "Cut" },
                        'v' => new[] { "Paste" },
                        'z' => new[] { hasShift ? "Redo" : "Undo" },
                        'y' => new[] { "Redo" },
                        _ => null
                    };
                }

                await SendAsync(ws, new {
                    id = ++id,
                    method = "Input.dispatchKeyEvent",
                    @params = new {
                        type = downType,
                        key = k,
                        code = codeStr,
                        text = keyText,
                        unmodifiedText = keyText,
                        windowsVirtualKeyCode = keyCode,
                        nativeVirtualKeyCode = keyCode,
                        modifiers = mods,
                        commands
                    }
                }, cts.Token);

                if (isPrintable)
                {
                    await SendAsync(ws, new {
                        id = ++id,
                        method = "Input.dispatchKeyEvent",
                        @params = new {
                            type = "char",
                            key = k,
                            code = codeStr,
                            text = keyText,
                            unmodifiedText = keyText,
                            windowsVirtualKeyCode = keyCode,
                            nativeVirtualKeyCode = keyCode,
                            modifiers = mods
                        }
                    }, cts.Token);
                }

                var kuId = ++id;
                await SendAsync(ws, new { id = kuId, method = "Input.dispatchKeyEvent", @params = new { type = "keyUp", key = k, code = codeStr, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode, modifiers = mods } }, cts.Token);
                await ReceiveByIdAsync(ws, kuId, cts.Token);
            }
            else if (action == "clipboard_copy")
            {
                var jsCopy = "(() => { try { document.execCommand('copy'); const a = document.activeElement; if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA')) { const s = a.selectionStart || 0, e = a.selectionEnd || 0; if (e > s) return a.value.substring(s, e); } return window.getSelection()?.toString() || ''; } catch { return ''; } })()";
                var cId = ++id;
                await SendAsync(ws, new { id = cId, method = "Runtime.evaluate", @params = new { expression = jsCopy, returnByValue = true } }, cts.Token);
                await ReceiveByIdAsync(ws, cId, cts.Token);
            }
            else if (action == "clipboard_paste" && req.Text != null)
            {
                var txtId = ++id;
                await SendAsync(ws, new { id = txtId, method = "Input.insertText", @params = new { text = req.Text } }, cts.Token);
                await ReceiveByIdAsync(ws, txtId, cts.Token);
            }
            else if (action == "navigate" && !string.IsNullOrWhiteSpace(req.Text))
            {
                if (req.Width is > 0 && req.Height is > 0)
                {
                    var isMobileNav = req.IsMobile == true || string.Equals(req.DeviceType, "phone", StringComparison.OrdinalIgnoreCase);
                    var (w, h) = ScreencastHub.NormalizeViewport(req.Width.Value, req.Height.Value, 1440, 813, isMobileNav);
                    var backingScale = ScreencastHub.ComputeBackingScale(w, h, 1440, 813, isMobileNav);
                    var orientationType = w > h ? "landscapePrimary" : "portraitPrimary";
                    var orientationAngle = w > h ? 90 : 0;
                    await SendAsync(ws, new { id = ++id, method = "Network.enable", @params = new { } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Network.setUserAgentOverride", @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileNav) }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setUserAgentOverride", @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileNav) }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setTouchEmulationEnabled", @params = new { enabled = isMobileNav, maxTouchPoints = isMobileNav ? 5 : 0 } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setEmitTouchEventsForMouse", @params = new { enabled = isMobileNav, configuration = isMobileNav ? "mobile" : "desktop" } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setDeviceMetricsOverride", @params = new { width = w, height = h, deviceScaleFactor = 1, scale = backingScale, mobile = isMobileNav, screenWidth = w, screenHeight = h, screenOrientation = new { type = orientationType, angle = orientationAngle } } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.resetPageScaleFactor" }, cts.Token);
                }
                var navUrl = req.Text.Trim();
                if (!navUrl.Contains("://") && !navUrl.StartsWith("about:"))
                {
                    navUrl = navUrl.Contains(".") && !navUrl.Contains(" ") ? $"https://{navUrl}" : $"https://www.google.com/search?q={Uri.EscapeDataString(navUrl)}";
                }
                var navId = ++id;
                await SendAsync(ws, new { id = navId, method = "Page.navigate", @params = new { url = navUrl } }, cts.Token);
                await ReceiveByIdAsync(ws, navId, cts.Token);
            }
            else if (action == "stop")
            {
                var stopId = ++id;
                await SendAsync(ws, new { id = stopId, method = "Page.stopLoading" }, cts.Token);
                await ReceiveByIdAsync(ws, stopId, cts.Token);
            }
            else if (action == "reload")
            {
                var isMobileRel = req.IsMobile == true || string.Equals(req.DeviceType, "phone", StringComparison.OrdinalIgnoreCase);
                if (req.Width is > 0 && req.Height is > 0)
                {
                    var (w, h) = ScreencastHub.NormalizeViewport(req.Width.Value, req.Height.Value, 1440, 813, isMobileRel);
                    var backingScale = ScreencastHub.ComputeBackingScale(w, h, 1440, 813, isMobileRel);
                    var orientationType = w > h ? "landscapePrimary" : "portraitPrimary";
                    var orientationAngle = w > h ? 90 : 0;
                    await SendAsync(ws, new { id = ++id, method = "Network.enable", @params = new { } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Network.setUserAgentOverride", @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileRel) }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setUserAgentOverride", @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileRel) }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setTouchEmulationEnabled", @params = new { enabled = isMobileRel, maxTouchPoints = isMobileRel ? 5 : 0 } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setEmitTouchEventsForMouse", @params = new { enabled = isMobileRel, configuration = isMobileRel ? "mobile" : "desktop" } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setDeviceMetricsOverride", @params = new { width = w, height = h, deviceScaleFactor = 1, scale = backingScale, mobile = isMobileRel, screenWidth = w, screenHeight = h, screenOrientation = new { type = orientationType, angle = orientationAngle } } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.resetPageScaleFactor" }, cts.Token);
                }
                var relId = ++id;
                await SendAsync(ws, new { id = relId, method = "Page.reload", @params = new { ignoreCache = req.ReloadOnModeChange == true || isMobileRel } }, cts.Token);
                await ReceiveByIdAsync(ws, relId, cts.Token);
            }
            else if (action == "back" || action == "forward")
            {
                if (req.Width is > 0 && req.Height is > 0)
                {
                    var isMobileHist = req.IsMobile == true || string.Equals(req.DeviceType, "phone", StringComparison.OrdinalIgnoreCase);
                    var (w, h) = ScreencastHub.NormalizeViewport(req.Width.Value, req.Height.Value, 1440, 813, isMobileHist);
                    var backingScale = ScreencastHub.ComputeBackingScale(w, h, 1440, 813, isMobileHist);
                    var orientationType = w > h ? "landscapePrimary" : "portraitPrimary";
                    var orientationAngle = w > h ? 90 : 0;
                    await SendAsync(ws, new { id = ++id, method = "Network.enable", @params = new { } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Network.setUserAgentOverride", @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileHist) }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setUserAgentOverride", @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileHist) }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setTouchEmulationEnabled", @params = new { enabled = isMobileHist, maxTouchPoints = isMobileHist ? 5 : 0 } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setEmitTouchEventsForMouse", @params = new { enabled = isMobileHist, configuration = isMobileHist ? "mobile" : "desktop" } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.setDeviceMetricsOverride", @params = new { width = w, height = h, deviceScaleFactor = 1, scale = backingScale, mobile = isMobileHist, screenWidth = w, screenHeight = h, screenOrientation = new { type = orientationType, angle = orientationAngle } } }, cts.Token);
                    await SendAsync(ws, new { id = ++id, method = "Emulation.resetPageScaleFactor" }, cts.Token);
                }
                var evalId = ++id;
                var js = action == "back" ? "window.history.back()" : "window.history.forward()";
                await SendAsync(ws, new { id = evalId, method = "Runtime.evaluate", @params = new { expression = js } }, cts.Token);
                await ReceiveByIdAsync(ws, evalId, cts.Token);
            }
            else if (action == "find")
            {
                var qText = (req.Text ?? "").Trim();
                var findId = ++id;
                if (string.IsNullOrEmpty(qText))
                {
                    await SendAsync(ws, new { id = findId, method = "Runtime.evaluate", @params = new { expression = "try { window.getSelection()?.removeAllRanges(); } catch {}" } }, cts.Token);
                }
                else
                {
                    var backwards = string.Equals(req.Key, "prev", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                    var qJson = JsonSerializer.Serialize(qText);
                    var jsFind = $$"""(() => { try { const found = window.find({{qJson}}, false, {{backwards}}, true, false, true, false); const sel = window.getSelection(); if (sel && sel.rangeCount > 0) { const el = sel.anchorNode?.parentElement; if (el) el.scrollIntoView({ block: 'center', behavior: 'instant' }); } return found; } catch { return false; } })()""";
                    await SendAsync(ws, new { id = findId, method = "Runtime.evaluate", @params = new { expression = jsFind } }, cts.Token);
                }
                await ReceiveByIdAsync(ws, findId, cts.Token);
            }
            else if (action == "new_tab")
            {
                var newUrl = !string.IsNullOrWhiteSpace(req.Text) ? req.Text.Trim() : "https://www.google.com";
                if (!newUrl.Contains("://") && !newUrl.StartsWith("about:"))
                {
                    newUrl = newUrl.Contains(".") && !newUrl.Contains(" ") ? $"https://{newUrl}" : $"https://www.google.com/search?q={Uri.EscapeDataString(newUrl)}";
                }
                var createId = ++id;
                object createParams = !string.IsNullOrEmpty(browserContextId)
                    ? new { url = newUrl, browserContextId }
                    : new { url = newUrl };
                await SendAsync(ws, new { id = createId, method = "Target.createTarget", @params = createParams }, cts.Token);
                var createBytes = await ReceiveByIdAsync(ws, createId, cts.Token);
                if (createBytes is not null)
                {
                    using var doc = JsonDocument.Parse(createBytes);
                    if (doc.RootElement.TryGetProperty("result", out var res) &&
                        res.TryGetProperty("targetId", out var tid))
                    {
                        createdTargetId = tid.GetString();
                    }
                }
                if (!string.IsNullOrEmpty(createdTargetId))
                {
                    var actId = ++id;
                    _ = SendAsync(ws, new { id = actId, method = "Target.activateTarget", @params = new { targetId = createdTargetId } }, CancellationToken.None);
                }
            }
            else if (action == "close_tab")
            {
                var closeTargetId = !string.IsNullOrWhiteSpace(req.PageId) ? req.PageId : targetId;
                var closeId = ++id;
                await SendAsync(ws, new { id = closeId, method = "Target.closeTarget", @params = new { targetId = closeTargetId } }, cts.Token);
                await ReceiveByIdAsync(ws, closeId, cts.Token);
            }
            else if (action == "activate_tab")
            {
                var actTargetId = !string.IsNullOrWhiteSpace(req.PageId) ? req.PageId : targetId;
                var actId = ++id;
                await SendAsync(ws, new { id = actId, method = "Target.activateTarget", @params = new { targetId = actTargetId } }, cts.Token);
                await ReceiveByIdAsync(ws, actId, cts.Token);
            }
            else if (action == "resize" && req.Width is > 0 && req.Height is > 0)
            {
                var isMobileResize = req.IsMobile == true || string.Equals(req.DeviceType, "phone", StringComparison.OrdinalIgnoreCase);
                var (w, h) = ScreencastHub.NormalizeViewport(req.Width.Value, req.Height.Value, 1440, 813, isMobileResize);
                var backingScale = ScreencastHub.ComputeBackingScale(w, h, 1440, 813, isMobileResize);
                await SendAsync(ws, new { id = ++id, method = "Network.enable", @params = new { } }, cts.Token);
                await SendAsync(ws, new
                {
                    id = ++id,
                    method = "Network.setUserAgentOverride",
                    @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileResize)
                }, cts.Token);
                await SendAsync(ws, new
                {
                    id = ++id,
                    method = "Emulation.setUserAgentOverride",
                    @params = ScreencastHub.BuildUserAgentOverrideParams(isMobileResize)
                }, cts.Token);
                await SendAsync(ws, new
                {
                    id = ++id,
                    method = "Emulation.setTouchEmulationEnabled",
                    @params = new { enabled = isMobileResize, maxTouchPoints = isMobileResize ? 5 : 0 }
                }, cts.Token);
                await SendAsync(ws, new
                {
                    id = ++id,
                    method = "Emulation.setEmitTouchEventsForMouse",
                    @params = new { enabled = isMobileResize, configuration = isMobileResize ? "mobile" : "desktop" }
                }, cts.Token);
                var orientationType = w > h ? "landscapePrimary" : "portraitPrimary";
                var orientationAngle = w > h ? 90 : 0;
                var rId = ++id;
                await SendAsync(ws, new
                {
                    id = rId,
                    method = "Emulation.setDeviceMetricsOverride",
                    @params = new
                    {
                        width = w,
                        height = h,
                        deviceScaleFactor = 1,
                        scale = backingScale,
                        mobile = isMobileResize,
                        screenWidth = w,
                        screenHeight = h,
                        screenOrientation = new { type = orientationType, angle = orientationAngle }
                    }
                }, cts.Token);
                await SendAsync(ws, new { id = ++id, method = "Emulation.resetPageScaleFactor" }, cts.Token);
                if (req.ReloadOnModeChange == true)
                {
                    await SendAsync(ws, new { id = ++id, method = "Page.reload", @params = new { ignoreCache = true } }, cts.Token);
                }
                await ReceiveByIdAsync(ws, rId, cts.Token);
            }

            return (true, createdTargetId);
        }
        catch
        {
            return (false, null);
        }
    }

    public sealed record BrowserHistoryItem(int Id, string Title, string Url, bool IsCurrent);

    private static async Task<IReadOnlyList<BrowserHistoryItem>> GetNavigationHistoryAsync(string cdpUrl, string targetId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);
            await SendAsync(ws, new { id = 77, method = "Page.getNavigationHistory" }, cts.Token);
            var bytes = await ReceiveByIdAsync(ws, 77, cts.Token);
            if (bytes is null) return [];
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("result", out var res)) return [];
            var currentIdx = res.TryGetProperty("currentIndex", out var ci) ? ci.GetInt32() : -1;
            var list = new List<BrowserHistoryItem>();
            if (res.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var el in entries.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : idx;
                    var url = el.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? "" : "";
                    var title = el.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new BrowserHistoryItem(id, string.IsNullOrWhiteSpace(title) ? url : title, url, idx == currentIdx));
                    }
                    idx++;
                }
            }
            list.Reverse();
            return list;
        }
        catch
        {
            return [];
        }
    }

    private static Uri PageSocket(string cdpUrl, string targetId)
    {
        var cdp = new Uri(cdpUrl);
        return new Uri($"ws://{cdp.Authority}/devtools/page/{Uri.EscapeDataString(targetId)}");
    }

    private static async Task<string?> CaptureScreenshotAsync(string cdpUrl, string targetId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);
        // A tab behind another profile's window on the shared desktop is "hidden": Chromium throttles it and a
        // capture can wait seconds for a frame. Emulating focus/active lifecycle for this session makes the
        // renderer produce a frame now; both are per-session and end when this socket closes.
        await SendAsync(ws, new { id = 10, method = "Emulation.setFocusEmulationEnabled", @params = new { enabled = true } }, cts.Token);
        await SendAsync(ws, new { id = 11, method = "Page.setWebLifecycleState", @params = new { state = "active" } }, cts.Token);
        // fromSurface + optimizeForSpeed: take the composited surface instead of waiting for a fresh frame.
        await SendAsync(ws, new { id = 1, method = "Page.captureScreenshot", @params = new { format = "jpeg", quality = 92, fromSurface = true, optimizeForSpeed = true } }, cts.Token);
        while (ws.State == WebSocketState.Open)
        {
            var bytes = await ReceiveAsync(ws, cts.Token);
            if (bytes is null)
            {
                break;
            }
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1)
            {
                return doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("data", out var d) ? d.GetString() : null;
            }
        }
        return null;
    }

    private static async Task<byte[]?> ReceiveByIdAsync(WebSocket ws, int expectedId, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open)
        {
            var bytes = await ReceiveAsync(ws, ct);
            if (bytes is null) return null;
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
            {
                return bytes;
            }
        }
        return null;
    }

    private const int MaxCdpMessageBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions CdpJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static Task SendAsync(WebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, CdpJsonOptions), WebSocketMessageType.Text, true, ct);

    private static async Task<byte[]?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > MaxCdpMessageBytes)
            {
                throw new WebSocketException("CDP message exceeds limit");
            }
        } while (!r.EndOfMessage);
        return ms.ToArray();
    }
}
