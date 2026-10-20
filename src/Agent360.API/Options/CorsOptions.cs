using Microsoft.Extensions.Configuration;

namespace Agent360.API.Options;

public sealed class CorsOptions
{
    public const string Section = "Cors";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Reads Cors:AllowedOrigins as an array (Cors__AllowedOrigins__0=...) or as one value separated by
    /// ';' or ',' (Cors__AllowedOrigins="a;b"). Also reads CORS_ALLOWED_ORIGINS environment variable.</summary>
    public static string[] Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section).GetSection(nameof(AllowedOrigins));
        var envDirect = configuration["CORS_ALLOWED_ORIGINS"];
        var rawValues = new List<string>();
        if (!string.IsNullOrWhiteSpace(envDirect)) rawValues.Add(envDirect);
        if (section.Value is { } scalar && !string.IsNullOrWhiteSpace(scalar)) rawValues.Add(scalar);
        rawValues.AddRange(section.GetChildren().Select(c => c.Value ?? "").Where(v => !string.IsNullOrWhiteSpace(v)));

        return rawValues
            .SelectMany(v => v.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
