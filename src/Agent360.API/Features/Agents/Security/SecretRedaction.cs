using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agent360.API.Features.Agents.Security;

/// <summary>
/// Masks credential-looking values in JSON returned to API callers (agent configs, channel configs, MCP client
/// env/headers, provider catalogs). Values are replaced with <see cref="Mask"/> so the UI can still show "configured".
/// Writes that echo the mask back are restored from the live config by <see cref="RestoreMaskedValues"/>.
/// </summary>
public static partial class SecretRedaction
{
    public const string Mask = "********";

    [GeneratedRegex(@"(token|secret|password|passwd|api[_-]?key|apikey|private[_-]?key|client[_-]?secret|credential|cookie|authorization|webhook[_-]?url|access[_-]?key|dsn|^key_prefix$|api_key_prefix)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    // Keys whose names look sensitive but carry only harmless metadata.
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "max_tokens", "token_count_estimate_divisor", "max_input_tokens", "max_output_tokens", "token_limit",
        "prompt_tokens", "completion_tokens", "total_tokens", "require_api_key", "discovery_requires_auth",
        "api_key_configured", "has_token", "token_type", "max_context_tokens", "context_window_tokens",
    };

    public static bool IsSensitiveName(string name) =>
        !AllowedNames.Contains(name) && SensitiveName().IsMatch(name) && !name.EndsWith("_count", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the JSON text with sensitive string values masked; non-JSON input is returned unchanged.</summary>
    public static string RedactJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch { return json; }
        if (node is null) return json;
        return Redact(node) ? node.ToJsonString() : json;
    }

    public static byte[] RedactJson(byte[] utf8)
    {
        if (utf8.Length == 0) return utf8;
        JsonNode? node;
        try { node = JsonNode.Parse(utf8); } catch { return utf8; }
        if (node is null) return utf8;
        return Redact(node) ? System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()) : utf8;
    }

    /// <summary>Masks in place; returns true when anything changed.</summary>
    public static bool Redact(JsonNode node, string? name = null)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var s) && IsSensitiveName(key) && ShouldMask(s))
                    {
                        obj[key] = Mask;
                        changed = true;
                    }
                    else if (child is JsonArray arr && IsSensitiveName(key))
                    {
                        for (var i = 0; i < arr.Count; i++)
                        {
                            if (arr[i] is JsonValue av && av.TryGetValue<string>(out var a) && ShouldMask(a)) { arr[i] = Mask; changed = true; }
                        }
                    }
                    else if (child is not null)
                    {
                        changed |= Redact(child, key);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null) changed |= Redact(child, name);
                }
                break;
        }
        return changed;
    }

    private static bool ShouldMask(string value) =>
        !string.IsNullOrEmpty(value) && value != Mask && !value.All(c => c == '*')
        // env/config references are not secrets themselves
        && !value.StartsWith("${", StringComparison.Ordinal) && !value.StartsWith("env:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For PUT/PATCH/POST bodies built from a redacted GET (the UI merges the read result back): replace every
    /// <see cref="Mask"/> value with the live value at the same JSON path so a save never overwrites a secret with stars.
    /// </summary>
    public static string RestoreMaskedValues(string incomingJson, string? liveJson)
    {
        if (string.IsNullOrWhiteSpace(incomingJson) || !incomingJson.Contains(Mask, StringComparison.Ordinal)) return incomingJson;
        JsonNode? incoming, live = null;
        try { incoming = JsonNode.Parse(incomingJson); } catch { return incomingJson; }
        try { if (!string.IsNullOrWhiteSpace(liveJson)) live = JsonNode.Parse(liveJson); } catch { live = null; }
        if (incoming is null) return incomingJson;
        Restore(incoming, live);
        return incoming.ToJsonString();
    }

    private static void Restore(JsonNode incoming, JsonNode? live)
    {
        switch (incoming)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    var liveChild = (live as JsonObject)?[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s) && s == Mask)
                    {
                        // Unknown live value: drop the field rather than persisting the mask.
                        if (liveChild is JsonValue) obj[key] = liveChild.DeepClone(); else obj.Remove(key);
                    }
                    else if (child is not null)
                    {
                        Restore(child, liveChild);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var liveItem = live is JsonArray la && i < la.Count ? la[i] : null;
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == Mask)
                    {
                        if (liveItem is JsonValue) arr[i] = liveItem.DeepClone();
                    }
                    else if (arr[i] is not null)
                    {
                        Restore(arr[i]!, liveItem);
                    }
                }
                break;
        }
    }
}
