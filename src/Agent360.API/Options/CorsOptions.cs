using Microsoft.Extensions.Configuration;

namespace Agent360.API.Options;

public sealed class CorsOptions
{
    public const string Section = "Cors";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Reads Cors:AllowedOrigins as an array (Cors__AllowedOrigins__0=...) or as one value separated by
    /// ';' or ',' (Cors__AllowedOrigins="a;b"). The array binder alone yields nothing for the scalar form.</summary>
    public static string[] Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section).GetSection(nameof(AllowedOrigins));
        var values = section.Value is { } scalar ? [scalar] : section.GetChildren().Select(c => c.Value ?? "");
        return values
            .SelectMany(v => v.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
