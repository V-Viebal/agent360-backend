namespace Agent360.API.Options;

public sealed class ComposioOptions
{
    public const string SectionName = "Composio";

    public string? WebhookSecret { get; set; }

    public string? GlobalApiKey { get; set; }

    public int MaxTrackingMinutes { get; set; } = 10;

    public int PollIntervalSeconds { get; set; } = 5;
}
