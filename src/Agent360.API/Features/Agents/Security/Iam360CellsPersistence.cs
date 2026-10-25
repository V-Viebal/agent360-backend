using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent360.API.Features.Agents.Security;

public sealed record PersistedIam360CellsState(
    Dictionary<string, string> IdentityStatusOverrides,
    Dictionary<string, string> OrgEnforcementOverrides,
    Dictionary<string, Dictionary<string, string>> OrgRoleOverrides,
    List<Iam360SignedIntentEnvelopeDto> SignedIntentsLedger);

public sealed class Iam360CellsPersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object FileLock = new();
    private readonly string _storageDir;
    private readonly string _stateFilePath;
    private readonly string _keyFilePath;
    private readonly RSA _signingKey;
    private readonly string _publicKeyB64;
    private readonly string _keyId;

    public Iam360CellsPersistence(IWebHostEnvironment env)
    {
        _storageDir = Path.Combine(env.ContentRootPath, "_storage");
        Directory.CreateDirectory(_storageDir);
        _stateFilePath = Path.Combine(_storageDir, "iam360_cells_state.json");
        _keyFilePath = Path.Combine(_storageDir, "iam360_signing_key.bin");
        (_signingKey, _publicKeyB64, _keyId) = LoadOrCreateSigningKey(_keyFilePath);
    }

    public string PublicKeyB64 => _publicKeyB64;
    public string KeyId => _keyId;

    public PersistedIam360CellsState LoadState()
    {
        lock (FileLock)
        {
            if (!File.Exists(_stateFilePath))
            {
                return new PersistedIam360CellsState(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
                    []);
            }

            try
            {
                var raw = File.ReadAllText(_stateFilePath);
                var loaded = JsonSerializer.Deserialize<PersistedIam360CellsState>(raw);
                if (loaded is not null)
                {
                    return new PersistedIam360CellsState(
                        new Dictionary<string, string>(loaded.IdentityStatusOverrides ?? [], StringComparer.OrdinalIgnoreCase),
                        new Dictionary<string, string>(loaded.OrgEnforcementOverrides ?? [], StringComparer.OrdinalIgnoreCase),
                        (loaded.OrgRoleOverrides ?? []).ToDictionary(
                            k => k.Key,
                            v => new Dictionary<string, string>(v.Value ?? [], StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase),
                        loaded.SignedIntentsLedger ?? []);
                }
            }
            catch
            {
                // Corrupt file fallback
            }

            return new PersistedIam360CellsState(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
                []);
        }
    }

    public void SaveState(PersistedIam360CellsState state)
    {
        lock (FileLock)
        {
            var tmpPath = _stateFilePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmpPath, _stateFilePath, overwrite: true);
        }
    }

    public Iam360SignedIntentEnvelopeDto CreateSignedIntent(
        string runtimeId,
        string agentId,
        string cellId,
        string intentType,
        IReadOnlyDictionary<string, object?> payload,
        string upstreamStatus = "persisted_and_signed")
    {
        var now = DateTimeOffset.UtcNow;
        var issuedAt = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var expiresAt = now.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var intentId = $"intent:{Guid.NewGuid():N}";

        var canonicalBody = JsonSerializer.Serialize(new
        {
            schema = "iam360/intent/v1",
            intent_id = intentId,
            runtime_id = runtimeId,
            agent_id = agentId,
            cell_id = cellId,
            type = intentType,
            issued_at = issuedAt,
            expires_at = expiresAt,
            payload
        });

        var bodyBytes = Encoding.UTF8.GetBytes(canonicalBody);
        var contentHash = "sha256:" + Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var sigBytes = _signingKey.SignData(bodyBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sigB64 = Convert.ToBase64String(sigBytes);

        return new Iam360SignedIntentEnvelopeDto(
            Schema: "iam360/intent/v1",
            IntentId: intentId,
            RuntimeId: runtimeId,
            AgentId: agentId,
            CellId: cellId,
            Type: intentType,
            IssuedAt: issuedAt,
            ExpiresAt: expiresAt,
            Payload: payload,
            Signature: new Iam360DetachedSignatureDto(
                Alg: "ed25519-compatible-rsa256",
                KeyId: _keyId,
                PublicKey: _publicKeyB64,
                ContentHash: contentHash,
                Value: sigB64),
            UpstreamSyncStatus: upstreamStatus,
            UpstreamTarget: "https://iam360.hmz.one/api/iam360/v2/runtime/intents");
    }

    private static (RSA Key, string PublicB64, string KeyId) LoadOrCreateSigningKey(string keyPath)
    {
        var rsa = RSA.Create(2048);
        try
        {
            if (File.Exists(keyPath))
            {
                var privBytes = File.ReadAllBytes(keyPath);
                rsa.ImportRSAPrivateKey(privBytes, out _);
            }
            else
            {
                var privBytes = rsa.ExportRSAPrivateKey();
                File.WriteAllBytes(keyPath, privBytes);
            }
        }
        catch
        {
            // Keep generated in-memory key if disk write is restricted
        }

        var pubBytes = rsa.ExportSubjectPublicKeyInfo();
        var pubB64 = Convert.ToBase64String(pubBytes);
        var keyId = "ed25519:" + Convert.ToHexString(SHA256.HashData(pubBytes)).ToLowerInvariant()[..24];
        return (rsa, pubB64, keyId);
    }
}
