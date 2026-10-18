namespace Agent360.API.Options;

public sealed class CorsOptions
{
    public const string Section = "Cors";

    private string[] _allowedOrigins = [];

    /// <summary>Accepts array binding or one env value separated by ';' / ',' (Cors__AllowedOrigins="a;b").</summary>
    public string[] AllowedOrigins
    {
        get => _allowedOrigins;
        set => _allowedOrigins = (value ?? [])
            .SelectMany(v => (v ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
    }
}
