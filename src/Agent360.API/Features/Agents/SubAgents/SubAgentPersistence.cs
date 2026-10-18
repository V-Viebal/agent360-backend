using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Agent360.API.Features.Agents.SubAgents;

/// <summary>Durable backing store behind <see cref="SubAgentStore"/>'s in-memory registry and ledger.</summary>
public interface ISubAgentPersistence
{
    string Kind { get; }
    (List<SubAgentBinding> Bindings, List<DelegationTask> Tasks) Load();
    Task UpsertBindingAsync(SubAgentBinding binding, IReadOnlyList<SubAgentBinding> all);
    Task DeleteBindingAsync(string parentAgentId, string childAgentId, IReadOnlyList<SubAgentBinding> all);
    Task SaveTaskAsync(DelegationTask task, IReadOnlyList<DelegationTask> all);
    Task ArchiveTasksAsync(IReadOnlyCollection<DelegationTask> archived, IReadOnlyList<DelegationTask> remaining);
}

/// <summary>Legacy JSON files (atomic temp+replace). Kept for local development and tests.</summary>
public sealed class FileSubAgentPersistence(string dir) : ISubAgentPersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _bindingsPath = Path.Combine(dir, "subagent_bindings.json");
    private readonly string _ledgerPath = Path.Combine(dir, "delegation_tasks.json");

    public string Kind => "file";

    public (List<SubAgentBinding>, List<DelegationTask>) Load() => (LoadFile<SubAgentBinding>(_bindingsPath), LoadFile<DelegationTask>(_ledgerPath));

    public Task UpsertBindingAsync(SubAgentBinding binding, IReadOnlyList<SubAgentBinding> all) => SaveFileAsync(_bindingsPath, all);

    public Task DeleteBindingAsync(string parentAgentId, string childAgentId, IReadOnlyList<SubAgentBinding> all) => SaveFileAsync(_bindingsPath, all);

    public Task SaveTaskAsync(DelegationTask task, IReadOnlyList<DelegationTask> all) => SaveFileAsync(_ledgerPath, all);

    public async Task ArchiveTasksAsync(IReadOnlyCollection<DelegationTask> archived, IReadOnlyList<DelegationTask> remaining)
    {
        foreach (var group in archived.GroupBy(t => t.UpdatedAt.UtcDateTime.ToString("yyyy-MM")))
        {
            var path = Path.Combine(dir, $"delegation_tasks.archive-{group.Key}.jsonl");
            await File.AppendAllLinesAsync(path, group.Select(t => JsonSerializer.Serialize(t)));
        }
        await SaveFileAsync(_ledgerPath, remaining);
    }

    internal static List<T> LoadFile<T>(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            File.Copy(path, path + $".corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
            return [];
        }
    }

    private static async Task SaveFileAsync<T>(string path, IReadOnlyList<T> items)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(items, JsonOpts));
        File.Move(tmp, path, true);
    }
}

/// <summary>
/// PostgreSQL tables <c>subagent_bindings</c>, <c>delegation_tasks</c> and <c>delegation_tasks_archive</c>
/// (plan section 11). Key columns are relational for querying/indexing; the full record is kept in <c>doc</c> (jsonb)
/// so the wire contract stays identical. Schema is created idempotently; legacy JSON files are imported once.
/// </summary>
public sealed class PostgresSubAgentPersistence : ISubAgentPersistence
{
    private readonly NpgsqlDataSource _db;
    private readonly string _schema;

    public PostgresSubAgentPersistence(string connectionString, string schema = "agent360_subagents")
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException("invalid schema name", nameof(schema));
        _schema = schema;
        _db = NpgsqlDataSource.Create(connectionString);
    }

    public string Kind => "postgres";

    public void EnsureSchema()
    {
        using var cmd = _db.CreateCommand($"""
            CREATE SCHEMA IF NOT EXISTS {_schema};
            CREATE TABLE IF NOT EXISTS {_schema}.subagent_bindings (
                id text PRIMARY KEY,
                parent_agent_id text NOT NULL,
                child_agent_id text NOT NULL,
                runtime_instance_id text NOT NULL,
                enabled boolean NOT NULL,
                doc jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                CONSTRAINT uq_subagent_bindings_parent_child UNIQUE (parent_agent_id, child_agent_id));
            CREATE TABLE IF NOT EXISTS {_schema}.delegation_tasks (
                id text PRIMARY KEY,
                parent_agent_id text NOT NULL,
                child_agent_id text NOT NULL,
                mode text NOT NULL,
                source text NOT NULL,
                status text NOT NULL,
                attempt integer NOT NULL,
                native_task_id text NULL,
                session_id text NOT NULL,
                root_run_id text NOT NULL,
                doc jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                completed_at timestamptz NULL);
            CREATE INDEX IF NOT EXISTS ix_delegation_tasks_parent_created ON {_schema}.delegation_tasks (parent_agent_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_delegation_tasks_status ON {_schema}.delegation_tasks (status);
            CREATE INDEX IF NOT EXISTS ix_delegation_tasks_native ON {_schema}.delegation_tasks (native_task_id) WHERE native_task_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS {_schema}.delegation_tasks_archive (
                id text PRIMARY KEY,
                parent_agent_id text NOT NULL,
                child_agent_id text NOT NULL,
                status text NOT NULL,
                doc jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                archived_at timestamptz NOT NULL DEFAULT now());
            """);
        cmd.ExecuteNonQuery();
    }

    /// <summary>One-time import of legacy JSON files when the tables are still empty. Returns imported counts.</summary>
    public (int Bindings, int Tasks) ImportLegacyFiles(string dir)
    {
        var bindingsPath = Path.Combine(dir, "subagent_bindings.json");
        var ledgerPath = Path.Combine(dir, "delegation_tasks.json");
        if (!File.Exists(bindingsPath) && !File.Exists(ledgerPath)) return (0, 0);
        using (var count = _db.CreateCommand($"SELECT (SELECT count(*) FROM {_schema}.subagent_bindings) + (SELECT count(*) FROM {_schema}.delegation_tasks)"))
        {
            if ((long)count.ExecuteScalar()! > 0) return (0, 0);
        }
        var bindings = FileSubAgentPersistence.LoadFile<SubAgentBinding>(bindingsPath);
        var tasks = FileSubAgentPersistence.LoadFile<DelegationTask>(ledgerPath);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var b in bindings.GroupBy(b => (b.ParentAgentId, b.ChildAgentId)).Select(g => g.OrderBy(x => x.UpdatedAt).Last()))
            UpsertBinding(conn, tx, b);
        foreach (var t in tasks.GroupBy(t => t.Id).Select(g => g.OrderBy(x => x.UpdatedAt).Last()))
            UpsertTask(conn, tx, t);
        tx.Commit();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var p in new[] { bindingsPath, ledgerPath }.Where(File.Exists))
            File.Move(p, $"{p}.migrated-{stamp}");
        return (bindings.Count, tasks.Count);
    }

    public (List<SubAgentBinding>, List<DelegationTask>) Load()
    {
        var bindings = new List<SubAgentBinding>();
        using (var cmd = _db.CreateCommand($"SELECT doc::text FROM {_schema}.subagent_bindings"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) bindings.Add(JsonSerializer.Deserialize<SubAgentBinding>(r.GetString(0))!);
        var tasks = new List<DelegationTask>();
        using (var cmd = _db.CreateCommand($"SELECT doc::text FROM {_schema}.delegation_tasks"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) tasks.Add(JsonSerializer.Deserialize<DelegationTask>(r.GetString(0))!);
        return (bindings, tasks);
    }

    public async Task UpsertBindingAsync(SubAgentBinding binding, IReadOnlyList<SubAgentBinding> all)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        UpsertBinding(conn, tx, binding);
        await tx.CommitAsync();
    }

    public async Task DeleteBindingAsync(string parentAgentId, string childAgentId, IReadOnlyList<SubAgentBinding> all)
    {
        await using var cmd = _db.CreateCommand($"DELETE FROM {_schema}.subagent_bindings WHERE parent_agent_id = $1 AND child_agent_id = $2");
        cmd.Parameters.Add(new() { Value = parentAgentId });
        cmd.Parameters.Add(new() { Value = childAgentId });
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveTaskAsync(DelegationTask task, IReadOnlyList<DelegationTask> all)
    {
        await using var conn = await _db.OpenConnectionAsync();
        UpsertTask(conn, null, task);
    }

    public async Task ArchiveTasksAsync(IReadOnlyCollection<DelegationTask> archived, IReadOnlyList<DelegationTask> remaining)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var t in archived)
        {
            await using var ins = new NpgsqlCommand($"""
                INSERT INTO {_schema}.delegation_tasks_archive (id, parent_agent_id, child_agent_id, status, doc, created_at, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, doc = EXCLUDED.doc, updated_at = EXCLUDED.updated_at, archived_at = now()
                """, conn, tx);
            ins.Parameters.Add(new() { Value = t.Id });
            ins.Parameters.Add(new() { Value = t.ParentAgentId });
            ins.Parameters.Add(new() { Value = t.ChildAgentId });
            ins.Parameters.Add(new() { Value = t.Status });
            ins.Parameters.Add(new() { Value = JsonSerializer.Serialize(t), NpgsqlDbType = NpgsqlDbType.Jsonb });
            ins.Parameters.Add(new() { Value = t.CreatedAt });
            ins.Parameters.Add(new() { Value = t.UpdatedAt });
            await ins.ExecuteNonQueryAsync();
            await using var del = new NpgsqlCommand($"DELETE FROM {_schema}.delegation_tasks WHERE id = $1", conn, tx);
            del.Parameters.Add(new() { Value = t.Id });
            await del.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private void UpsertBinding(NpgsqlConnection conn, NpgsqlTransaction? tx, SubAgentBinding b)
    {
        using (var del = new NpgsqlCommand($"DELETE FROM {_schema}.subagent_bindings WHERE parent_agent_id = $1 AND child_agent_id = $2 AND id <> $3", conn, tx))
        {
            del.Parameters.Add(new() { Value = b.ParentAgentId });
            del.Parameters.Add(new() { Value = b.ChildAgentId });
            del.Parameters.Add(new() { Value = b.Id });
            del.ExecuteNonQuery();
        }
        using var cmd = new NpgsqlCommand($"""
            INSERT INTO {_schema}.subagent_bindings (id, parent_agent_id, child_agent_id, runtime_instance_id, enabled, doc, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (id) DO UPDATE SET enabled = EXCLUDED.enabled, runtime_instance_id = EXCLUDED.runtime_instance_id,
                doc = EXCLUDED.doc, updated_at = EXCLUDED.updated_at
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = b.Id });
        cmd.Parameters.Add(new() { Value = b.ParentAgentId });
        cmd.Parameters.Add(new() { Value = b.ChildAgentId });
        cmd.Parameters.Add(new() { Value = b.RuntimeInstanceId });
        cmd.Parameters.Add(new() { Value = b.Enabled });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(b), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = b.CreatedAt });
        cmd.Parameters.Add(new() { Value = b.UpdatedAt });
        cmd.ExecuteNonQuery();
    }

    private void UpsertTask(NpgsqlConnection conn, NpgsqlTransaction? tx, DelegationTask t)
    {
        using var cmd = new NpgsqlCommand($"""
            INSERT INTO {_schema}.delegation_tasks (id, parent_agent_id, child_agent_id, mode, source, status, attempt, native_task_id,
                session_id, root_run_id, doc, created_at, updated_at, completed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, attempt = EXCLUDED.attempt, native_task_id = EXCLUDED.native_task_id,
                session_id = EXCLUDED.session_id, doc = EXCLUDED.doc, updated_at = EXCLUDED.updated_at, completed_at = EXCLUDED.completed_at
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = t.Id });
        cmd.Parameters.Add(new() { Value = t.ParentAgentId });
        cmd.Parameters.Add(new() { Value = t.ChildAgentId });
        cmd.Parameters.Add(new() { Value = t.Mode });
        cmd.Parameters.Add(new() { Value = t.Source });
        cmd.Parameters.Add(new() { Value = t.Status });
        cmd.Parameters.Add(new() { Value = t.Attempt });
        cmd.Parameters.Add(new() { Value = (object?)t.NativeTaskId ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = t.SessionId });
        cmd.Parameters.Add(new() { Value = t.RootRunId });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(t), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = t.CreatedAt });
        cmd.Parameters.Add(new() { Value = t.UpdatedAt });
        cmd.Parameters.Add(new() { Value = (object?)t.CompletedAt ?? DBNull.Value });
        cmd.ExecuteNonQuery();
    }
}
