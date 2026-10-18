namespace Agent360.API.Features.Agents.Security;

public sealed record CredentialAuditResult(
    string KeyName,
    string SourceClass,
    bool Exists,
    bool FormatValid,
    string Scope,
    string AuditStatus);

public static class InfisicalCredentialAuditService
{
    private static readonly string[] RequiredKeys =
    [
        "LLM360_API_KEY",
        "LLM360_LOGTO_M2M_APP_ID",
        "LLM360_LOGTO_M2M_APP_SECRET"
    ];

    /// <summary>Agent-scoped Composio key from configuration (Infisical-rendered env); null when none is configured.</summary>
    public static string? ResolveAgentComposioToken(string agentId)
    {
        var cleanAgent = agentId.Replace("bot:agent360:", "").Trim().ToLowerInvariant();
        var agentEnvKey = $"COMPOSIO_API_KEY_{cleanAgent.Replace("-", "_").ToUpperInvariant()}";
        
        var specificKey = Environment.GetEnvironmentVariable(agentEnvKey);
        if (!string.IsNullOrWhiteSpace(specificKey))
        {
            return specificKey;
        }

        var globalKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY");
        if (!string.IsNullOrWhiteSpace(globalKey))
        {
            return globalKey;
        }

        // Fail closed: no built-in fallback key (the previous literal was public and is revoked).
        return null;
    }

    public static IReadOnlyList<CredentialAuditResult> AuditProviderCredentials(
        string providerId,
        IDictionary<string, string?>? configuredEnvironment = null)
    {
        var results = new List<CredentialAuditResult>();

        foreach (var key in RequiredKeys)
        {
            string? val = null;
            var sourceClass = "infisical";

            if (configuredEnvironment != null && configuredEnvironment.TryGetValue(key, out var envVal))
            {
                val = envVal;
                sourceClass = "process_env";
            }
            else
            {
                val = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    sourceClass = "injected_env";
                }
            }

            var exists = !string.IsNullOrWhiteSpace(val);
            var formatValid = exists && (val!.Length >= 8);
            var status = (exists && formatValid) ? "VERIFIED" : "AUDIT_FAILED_OR_MISSING";

            // Strict policy: Never expose actual raw secret values in result/audit records
            results.Add(new CredentialAuditResult(
                KeyName: key,
                SourceClass: sourceClass,
                Exists: exists,
                FormatValid: formatValid,
                Scope: $"provider:{providerId}",
                AuditStatus: status));
        }

        return results;
    }
}
