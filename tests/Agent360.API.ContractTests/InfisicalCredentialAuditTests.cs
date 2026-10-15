using Agent360.API.Features.Agents.Security;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class InfisicalCredentialAuditTests
{
    [Fact]
    public void AuditProviderCredentials_NeverExposesRawSecrets_AndChecksFormat()
    {
        var testEnv = new Dictionary<string, string?>
        {
            ["LLM360_API_KEY"] = "super-secret-production-token-12345",
            ["LLM360_LOGTO_M2M_APP_ID"] = "app-id-987654",
            ["LLM360_LOGTO_M2M_APP_SECRET"] = ""
        };

        var auditResults = InfisicalCredentialAuditService.AuditProviderCredentials(
            "llm360",
            testEnv);

        Assert.Equal(3, auditResults.Count);

        var apiKeyAudit = auditResults.First(r => r.KeyName == "LLM360_API_KEY");
        Assert.True(apiKeyAudit.Exists);
        Assert.True(apiKeyAudit.FormatValid);
        Assert.Equal("VERIFIED", apiKeyAudit.AuditStatus);
        Assert.Equal("process_env", apiKeyAudit.SourceClass);

        var secretAudit = auditResults.First(r => r.KeyName == "LLM360_LOGTO_M2M_APP_SECRET");
        Assert.False(secretAudit.Exists);
        Assert.Equal("AUDIT_FAILED_OR_MISSING", secretAudit.AuditStatus);

        // Verify that raw value is nowhere in the audit records
        var json = System.Text.Json.JsonSerializer.Serialize(auditResults);
        Assert.DoesNotContain("super-secret-production-token-12345", json);
    }
}
