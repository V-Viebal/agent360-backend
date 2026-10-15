namespace Agent360.API.Options;

public class LogtoOptions
{
    public const string LogtoSection = "Logto";

    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;

    // Reserved for outbound machine-to-machine or token-exchange flows.
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = [];

    public string OpenAPIClientId { get; set; } = string.Empty;
    // Scalar's public callback is environment-specific and must match the
    // redirect URI registered on the corresponding Logto SPA application.
    public string OpenAPIRedirectUri { get; set; } = string.Empty;

    public WebhookOptions Webhooks { get; set; } = new WebhookOptions();

    public class WebhookOptions
    {
        public string SignUpSigningKey { get; set; } = string.Empty;
    }
}