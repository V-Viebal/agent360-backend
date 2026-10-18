using Agent360.API.Features.Agents.SubAgents;
using Npgsql;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>
/// Real PostgreSQL round-trip for the sub-agent registry/ledger tables. Runs only when
/// AGENT360_SUBAGENTS_TEST_PG is set to a connection string (a throwaway schema is created and dropped).
/// </summary>
public sealed class SubAgentPostgresPersistenceTests : IAsyncLifetime
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("AGENT360_SUBAGENTS_TEST_PG");
    private readonly string _schema = "sa_test_" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-pg-" + Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Cs is null) return;
        await using var ds = NpgsqlDataSource.Create(Cs);
        await using var cmd = ds.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
        await cmd.ExecuteNonQueryAsync();
    }

    private static SubAgentBinding Binding(string child, bool enabled = true) => new(
        $"sab_{child}", "mons", child, "qwenpaw-core-3rd", "subagent", "Product Owner", ["requirements"], ["sync", "async"],
        1, 2, 300, false, enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DelegationTask Task(string id, string status, DateTimeOffset updated) => new(
        id, "run_1", "run_1", null, 1, ["mons", "ken"], "mons", "ken", "sync", "agent360", null, $"mons:to:ken:{id}",
        "objective", null, ["criterion"], status, 1, 2, "OUT", null, [], null, [], updated, updated, null);

    [Fact]
    public async Task Postgres_RoundTripsBindingsAndLedger_AcrossStoreInstances()
    {
        Assert.SkipWhen(Cs is null, "AGENT360_SUBAGENTS_TEST_PG not set");
        var pg = new PostgresSubAgentPersistence(Cs!, _schema);
        pg.EnsureSchema();
        pg.EnsureSchema(); // idempotent
        var store = new SubAgentStore(pg);
        Assert.Equal("postgres", store.PersistenceKind);

        await store.UpsertBindingAsync(Binding("ken"));
        await store.UpsertBindingAsync(Binding("ken", enabled: false)); // update, not duplicate
        await store.UpsertBindingAsync(Binding("mkai"));
        await store.DeleteBindingAsync("mons", "mkai");
        await store.SaveTaskAsync(Task("dlg_a", "under_review", DateTimeOffset.UtcNow));
        await store.SaveTaskAsync(Task("dlg_a", "accepted", DateTimeOffset.UtcNow));

        var reopened = new SubAgentStore(new PostgresSubAgentPersistence(Cs!, _schema));
        var bindings = await reopened.GetBindingsAsync("mons");
        Assert.Single(bindings);
        Assert.False(bindings[0].Enabled);
        var t = await reopened.GetTaskAsync("dlg_a");
        Assert.Equal("accepted", t!.Status);
        Assert.Equal(["mons", "ken"], t.CallChain);
        Assert.NotNull(await reopened.FindByNativeKeyAsync("mons", "mons:to:ken:dlg_a"));
    }

    [Fact]
    public async Task Postgres_CompactMovesTerminalTasksToArchiveTable()
    {
        Assert.SkipWhen(Cs is null, "AGENT360_SUBAGENTS_TEST_PG not set");
        var pg = new PostgresSubAgentPersistence(Cs!, _schema);
        pg.EnsureSchema();
        var store = new SubAgentStore(pg);
        await store.SaveTaskAsync(Task("dlg_old", "accepted", DateTimeOffset.UtcNow.AddDays(-60)));
        await store.SaveTaskAsync(Task("dlg_live", "running", DateTimeOffset.UtcNow.AddDays(-60)));

        Assert.Equal(1, await store.CompactAsync(TimeSpan.FromDays(30), 2000));

        var reopened = new SubAgentStore(new PostgresSubAgentPersistence(Cs!, _schema));
        Assert.Null(await reopened.GetTaskAsync("dlg_old"));
        Assert.NotNull(await reopened.GetTaskAsync("dlg_live"));
        await using var ds = NpgsqlDataSource.Create(Cs!);
        await using var cmd = ds.CreateCommand($"SELECT count(*) FROM {_schema}.delegation_tasks_archive WHERE id = 'dlg_old'");
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task Postgres_ImportsLegacyJsonOnce_AndRenamesFiles()
    {
        Assert.SkipWhen(Cs is null, "AGENT360_SUBAGENTS_TEST_PG not set");
        var file = new SubAgentStore(new FileSubAgentPersistence(_dir));
        await file.UpsertBindingAsync(Binding("ken"));
        await file.SaveTaskAsync(Task("dlg_legacy", "accepted", DateTimeOffset.UtcNow));

        var pg = new PostgresSubAgentPersistence(Cs!, _schema);
        pg.EnsureSchema();
        Assert.Equal((1, 1), pg.ImportLegacyFiles(_dir));
        Assert.Equal((0, 0), pg.ImportLegacyFiles(_dir));
        Assert.False(File.Exists(Path.Combine(_dir, "delegation_tasks.json")));
        Assert.NotEmpty(Directory.GetFiles(_dir, "delegation_tasks.json.migrated-*"));

        var store = new SubAgentStore(pg);
        Assert.NotNull(await store.GetTaskAsync("dlg_legacy"));
        Assert.Single(await store.GetBindingsAsync("mons"));
    }
}
