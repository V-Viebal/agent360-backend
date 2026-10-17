namespace Agent360.API.Options;

public sealed class QwenPawOptions
{
    public const string Section = "QwenPaw";

    public string BaseUrl { get; set; } = "https://ai.hmz.one";

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public string BrowserCdpUrl { get; set; } = "http://100.76.185.111:19223";

    /// <summary>Per-instance login for auth-enabled QwenPaw runtimes (keys: gm, climax, planthy).
    /// Bound from QwenPaw__Instances__&lt;key&gt;__Username/__Password; values come from Infisical.</summary>
    public Dictionary<string, QwenPawInstanceCredentials> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QwenPawInstanceCredentials
{
    public string? Username { get; set; }

    public string? Password { get; set; }
}
