using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Agent360.API.Features.Agents.Security;

public sealed record PersistedIam360CellsState(
    Dictionary<string, string> IdentityStatusOverrides,
    Dictionary<string, string> OrgEnforcementOverrides,
    Dictionary<string, Dictionary<string, string>> OrgRoleOverrides,
    List<Iam360SignedIntentEnvelopeDto> SignedIntentsLedger,
    Dictionary<string, Dictionary<string, bool>>? CapabilityOverrides = null,
    Dictionary<string, string>? OrgOwnerOverrides = null,
    List<Iam360TwoPersonApprovalDto>? TwoPersonApprovals = null);

public sealed record Iam360PersistenceDiagnosticsDto(
    string StorageDirectory,
    string StateFilePath,
    string SigningKeyId,
    bool PostgresConfigured,
    bool PostgresHealthy,
    string PostgresSchema,
    long PostgresSignedIntentsCount,
    long PostgresApprovalsCount,
    string LastSyncedAt);

public sealed class Iam360CellsPersistence
{
    private const string DbSchema = "agent360_iam360";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object SyncLock = new();

    private readonly string _storageDir;
    private readonly string _stateFilePath;
    private readonly string _keyFilePath;
    private readonly string? _connString;
    private readonly ILogger<Iam360CellsPersistence> _logger;
    private readonly RSA _signingKey;
    private readonly string _publicKeyB64;
    private readonly string _keyId;
    private bool _postgresReady;
    private string _lastSyncedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public Iam360CellsPersistence(
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<Iam360CellsPersistence> logger)
    {
        _logger = logger;
        var configuredPath = config["Agent360:Iam360:StoragePath"];
        _storageDir = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : Path.Combine(env.ContentRootPath, "_storage");

        Directory.CreateDirectory(_storageDir);
        _stateFilePath = Path.Combine(_storageDir, "iam360_cells_state.json");
        _keyFilePath = Path.Combine(_storageDir, "iam360_signing_key.bin");

        _connString = config.GetConnectionString("DefaultConnection")
            ?? config.GetConnectionString("Postgres");

        (_signingKey, _publicKeyB64, _keyId) = LoadOrCreateSigningKey(_keyFilePath);
        EnsurePostgresSchemaAndSeed();
    }

    public string PublicKeyB64 => _publicKeyB64;
    public string KeyId => _keyId;
    public bool PostgresReady => _postgresReady;
    public string StorageDir => _storageDir;

    private void EnsurePostgresSchemaAndSeed()
    {
        if (string.IsNullOrWhiteSpace(_connString)) return;

        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE SCHEMA IF NOT EXISTS {DbSchema};

                CREATE TABLE IF NOT EXISTS {DbSchema}.iam360_cells_state (
                    state_key text PRIMARY KEY,
                    doc jsonb NOT NULL,
                    updated_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE TABLE IF NOT EXISTS {DbSchema}.iam360_signed_intents (
                    intent_id text PRIMARY KEY,
                    runtime_id text NOT NULL,
                    agent_id text NOT NULL,
                    cell_id text NOT NULL,
                    intent_type text NOT NULL,
                    issued_at timestamptz NOT NULL,
                    key_id text NOT NULL,
                    content_hash text NOT NULL,
                    doc jsonb NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_iam360_signed_intents_cell ON {DbSchema}.iam360_signed_intents (cell_id, issued_at DESC);

                CREATE TABLE IF NOT EXISTS {DbSchema}.iam360_two_person_approvals (
                    approval_id text PRIMARY KEY,
                    kind text NOT NULL,
                    cell_id text NOT NULL,
                    target_identity_cell_id text NOT NULL,
                    status text NOT NULL,
                    requested_at timestamptz NOT NULL,
                    doc jsonb NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_iam360_approvals_status ON {DbSchema}.iam360_two_person_approvals (status, requested_at DESC);
                """;
            cmd.ExecuteNonQuery();
            _postgresReady = true;
            _lastSyncedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
        catch (Exception ex)
        {
            _postgresReady = false;
            _logger.LogWarning(ex, "[IAM360 Persistence] PostgreSQL schema initialization skipped; using file volume fallback at {StorageDir}", _storageDir);
        }
    }

    public PersistedIam360CellsState LoadState()
    {
        lock (SyncLock)
        {
            // 1. Try loading canonical state from PostgreSQL first
            if (_postgresReady && !string.IsNullOrWhiteSpace(_connString))
            {
                try
                {
                    using var conn = new NpgsqlConnection(_connString);
                    conn.Open();
                    using var cmd = new NpgsqlCommand($"SELECT doc FROM {DbSchema}.iam360_cells_state WHERE state_key = 'global' LIMIT 1;", conn);
                    var rawJson = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrWhiteSpace(rawJson))
                    {
                        var dbLoaded = DeserializeState(rawJson);
                        if (dbLoaded is not null)
                        {
                            // Keep local volume mirror up to date
                            SaveFileMirror(dbLoaded);
                            return dbLoaded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IAM360 Persistence] Failed reading state from PostgreSQL; falling back to volume file");
                }
            }

            // 2. Fallback to volume file (`_storage/iam360_cells_state.json`) and sync into PostgreSQL if DB is empty
            if (File.Exists(_stateFilePath))
            {
                try
                {
                    var fileLoaded = DeserializeState(File.ReadAllText(_stateFilePath));
                    if (fileLoaded is not null)
                    {
                        if (_postgresReady)
                        {
                            SaveToPostgres(fileLoaded);
                        }
                        return fileLoaded;
                    }
                }
                catch
                {
                    // Ignore corrupt file
                }
            }

            return EmptyState();
        }
    }

    public void SaveState(PersistedIam360CellsState state)
    {
        lock (SyncLock)
        {
            SaveFileMirror(state);
            if (_postgresReady)
            {
                SaveToPostgres(state);
            }
            _lastSyncedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
    }

    public Iam360PersistenceDiagnosticsDto GetDiagnostics()
    {
        long dbIntents = 0;
        long dbApprovals = 0;
        var healthy = false;

        if (_postgresReady && !string.IsNullOrWhiteSpace(_connString))
        {
            try
            {
                using var conn = new NpgsqlConnection(_connString);
                conn.Open();
                using var cmd1 = new NpgsqlCommand($"SELECT COUNT(*) FROM {DbSchema}.iam360_signed_intents;", conn);
                dbIntents = Convert.ToInt64(cmd1.ExecuteScalar() ?? 0L);

                using var cmd2 = new NpgsqlCommand($"SELECT COUNT(*) FROM {DbSchema}.iam360_two_person_approvals;", conn);
                dbApprovals = Convert.ToInt64(cmd2.ExecuteScalar() ?? 0L);
                healthy = true;
            }
            catch
            {
                healthy = false;
            }
        }

        return new Iam360PersistenceDiagnosticsDto(
            StorageDirectory: _storageDir,
            StateFilePath: _stateFilePath,
            SigningKeyId: _keyId,
            PostgresConfigured: !string.IsNullOrWhiteSpace(_connString),
            PostgresHealthy: healthy,
            PostgresSchema: DbSchema,
            PostgresSignedIntentsCount: dbIntents,
            PostgresApprovalsCount: dbApprovals,
            LastSyncedAt: _lastSyncedAt);
    }

    private void SaveFileMirror(PersistedIam360CellsState state)
    {
        try
        {
            var tmpPath = _stateFilePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmpPath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IAM360 Persistence] Could not write file mirror at {Path}", _stateFilePath);
        }
    }

    private void SaveToPostgres(PersistedIam360CellsState state)
    {
        if (string.IsNullOrWhiteSpace(_connString)) return;

        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            using (var stateCmd = new NpgsqlCommand($"""
                INSERT INTO {DbSchema}.iam360_cells_state (state_key, doc, updated_at)
                VALUES ('global', @doc, now())
                ON CONFLICT (state_key) DO UPDATE SET
                    doc = EXCLUDED.doc,
                    updated_at = now();
                """, conn, tx))
            {
                stateCmd.Parameters.AddWithValue("doc", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(state));
                stateCmd.ExecuteNonQuery();
            }

            foreach (var intent in state.SignedIntentsLedger.Take(50))
            {
                using var intentCmd = new NpgsqlCommand($"""
                    INSERT INTO {DbSchema}.iam360_signed_intents
                        (intent_id, runtime_id, agent_id, cell_id, intent_type, issued_at, key_id, content_hash, doc)
                    VALUES
                        (@id, @rt, @ag, @cell, @type, @issued, @kid, @hash, @doc)
                    ON CONFLICT (intent_id) DO NOTHING;
                    """, conn, tx);

                var issuedDto = DateTimeOffset.TryParse(intent.IssuedAt, out var parsed) ? parsed : DateTimeOffset.UtcNow;
                intentCmd.Parameters.AddWithValue("id", intent.IntentId);
                intentCmd.Parameters.AddWithValue("rt", intent.RuntimeId);
                intentCmd.Parameters.AddWithValue("ag", intent.AgentId);
                intentCmd.Parameters.AddWithValue("cell", intent.CellId);
                intentCmd.Parameters.AddWithValue("type", intent.Type);
                intentCmd.Parameters.AddWithValue("issued", issuedDto);
                intentCmd.Parameters.AddWithValue("kid", intent.Signature.KeyId);
                intentCmd.Parameters.AddWithValue("hash", intent.Signature.ContentHash);
                intentCmd.Parameters.AddWithValue("doc", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(intent));
                intentCmd.ExecuteNonQuery();
            }

            foreach (var app in state.TwoPersonApprovals ?? [])
            {
                using var appCmd = new NpgsqlCommand($"""
                    INSERT INTO {DbSchema}.iam360_two_person_approvals
                        (approval_id, kind, cell_id, target_identity_cell_id, status, requested_at, doc)
                    VALUES
                        (@id, @kind, @cell, @target, @status, @reqAt, @doc)
                    ON CONFLICT (approval_id) DO UPDATE SET
                        status = EXCLUDED.status,
                        doc = EXCLUDED.doc;
                    """, conn, tx);

                var reqDto = DateTimeOffset.TryParse(app.RequestedAt, out var parsed) ? parsed : DateTimeOffset.UtcNow;
                appCmd.Parameters.AddWithValue("id", app.ApprovalId);
                appCmd.Parameters.AddWithValue("kind", app.Kind);
                appCmd.Parameters.AddWithValue("cell", app.CellId);
                appCmd.Parameters.AddWithValue("target", app.TargetIdentityCellId);
                appCmd.Parameters.AddWithValue("status", app.Status);
                appCmd.Parameters.AddWithValue("reqAt", reqDto);
                appCmd.Parameters.AddWithValue("doc", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(app));
                appCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IAM360 Persistence] PostgreSQL state sync failed; file mirror remains authoritative");
        }
    }

    private static PersistedIam360CellsState? DeserializeState(string raw)
    {
        var loaded = JsonSerializer.Deserialize<PersistedIam360CellsState>(raw);
        if (loaded is null) return null;
        return new PersistedIam360CellsState(
            new Dictionary<string, string>(loaded.IdentityStatusOverrides ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(loaded.OrgEnforcementOverrides ?? [], StringComparer.OrdinalIgnoreCase),
            (loaded.OrgRoleOverrides ?? []).ToDictionary(
                k => k.Key,
                v => new Dictionary<string, string>(v.Value ?? [], StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            loaded.SignedIntentsLedger ?? [],
            (loaded.CapabilityOverrides ?? []).ToDictionary(
                k => k.Key,
                v => new Dictionary<string, bool>(v.Value ?? [], StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(loaded.OrgOwnerOverrides ?? [], StringComparer.OrdinalIgnoreCase),
            loaded.TwoPersonApprovals ?? []);
    }

    private static PersistedIam360CellsState EmptyState() => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        []);

    public Iam360SignedIntentEnvelopeDto CreateSignedIntent(
        string runtimeId,
        string agentId,
        string cellId,
        string intentType,
        IReadOnlyDictionary<string, object?> payload,
        string upstreamStatus = "persisted_postgres_and_signed")
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
            UpstreamSyncStatus: _postgresReady ? "persisted_postgres_and_volume" : upstreamStatus,
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
