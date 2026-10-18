namespace Agent360.API.Options;

public sealed class QwenPawOptions
{
    public const string Section = "QwenPaw";

    public string BaseUrl { get; set; } = "https://ai.hmz.one";

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Per-instance runtime settings (keys: core, gm, climax, planthy).
    /// Bound from QwenPaw__Instances__&lt;key&gt;__*; credentials come from Infisical.</summary>
    public Dictionary<string, QwenPawInstanceCredentials> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QwenPawInstanceCredentials
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Internal (Tailscale) endpoint, tried first.</summary>
    public string? InternalUrl { get; set; }

    /// <summary>Public endpoint, used only when the internal endpoint is unreachable.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Internal (tailnet/private) Chromium CDP endpoint for this instance's browser service.
    /// Public hostnames are rejected; the browser is never reached through a public route.</summary>
    public string? BrowserCdpUrl { get; set; }

    /// <summary>Coolify service name of the browser (e.g. chromium-browser-q4yu); overrides the built-in map.</summary>
    public string? BrowserService { get; set; }

    /// <summary>Public Selkies desktop origin (https only); overrides the built-in map.</summary>
    public string? BrowserViewerUrl { get; set; }

    /// <summary>Agent id -> Chromium --profile-directory, as the browser template's QWENPAW_AGENT_PROFILE_MAP_B64
    /// (plain JSON or base64 JSON). Overrides the built-in map so the template stays the single source.</summary>
    public string? BrowserProfileMap { get; set; }
}
