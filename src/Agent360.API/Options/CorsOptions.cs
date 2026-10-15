namespace Agent360.API.Options;

public sealed class CorsOptions
{
    public const string Section = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}