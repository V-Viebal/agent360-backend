namespace Agent360.API.Options;

public sealed class QwenPawOptions
{
    public const string Section = "QwenPaw";

    public string BaseUrl { get; set; } = "https://ai.hmz.one";

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public string BrowserCdpUrl { get; set; } = "http://100.76.185.111:19223";
}
