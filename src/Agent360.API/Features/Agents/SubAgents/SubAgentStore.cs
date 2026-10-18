using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.SubAgents;

/// <summary>
/// SubAgentBinding registry and DelegationTask ledger: an in-memory working set with serialized write-through to
/// <see cref="ISubAgentPersistence"/> (PostgreSQL tables in deployed environments, JSON files for local/tests).
/// </summary>
public sealed class SubAgentStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ISubAgentPersistence _persistence;
    private List<SubAgentBinding> _bindings;
    private List<DelegationTask> _tasks;

    /// <summary>"postgres" or "file".</summary>
    public string PersistenceKind => _persistence.Kind;

    /// <summary>In-process record of the last skill content fingerprint pushed per parent agent.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> SyncedSkillFingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SubAgentStore(IOptions<SubAgentOptions> options, IHostEnvironment env)
        : this(new FileSubAgentPersistence(ResolveDir(options.Value, env)))
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SubAgentStore(IOptions<SubAgentOptions> options, IHostEnvironment env, IConfiguration configuration, ILogger<SubAgentStore> logger)
        : this(CreatePersistence(options.Value, env, configuration, logger))
    {
    }

    public SubAgentStore(ISubAgentPersistence persistence)
    {
        _persistence = persistence;
        (_bindings, _tasks) = persistence.Load();
    }

    private static string ResolveDir(SubAgentOptions options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.StoragePath) ? options.StoragePath : Path.Combine(env.ContentRootPath, options.StoragePath);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ISubAgentPersistence CreatePersistence(SubAgentOptions options, IHostEnvironment env, IConfiguration configuration, ILogger logger)
    {
        var dir = ResolveDir(options, env);
        if (!string.Equals(options.Storage, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return new FileSubAgentPersistence(dir);
        }
        var cs = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException($"Agent360:SubAgents:Storage=postgres requires ConnectionStrings:{options.ConnectionStringName}");
        var pg = new PostgresSubAgentPersistence(cs, options.Schema);
        pg.EnsureSchema();
        var (b, t) = pg.ImportLegacyFiles(dir);
        if (b + t > 0)
        {
            logger.LogWarning("Imported legacy sub-agent JSON into PostgreSQL schema {Schema}: {Bindings} bindings, {Tasks} tasks", options.Schema, b, t);
        }
        return pg;
    }

    public async Task<IReadOnlyList<SubAgentBinding>> GetBindingsAsync(string parentAgentId)
    {
        await _lock.WaitAsync();
        try
        {
            return _bindings.Where(b => b.ParentAgentId == parentAgentId).OrderBy(b => b.ChildAgentId).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SubAgentBinding>> GetAllBindingsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _bindings.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Move terminal tasks older than <paramref name="retention"/> (or beyond <paramref name="maxTasks"/>)
    /// into monthly JSONL archives next to the ledger. Active tasks are never archived.
    /// </summary>
    public async Task<int> CompactAsync(TimeSpan retention, int maxTasks, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow) - retention;
        await _lock.WaitAsync();
        try
        {
            static bool Finished(DelegationTask t) => t.Status is DelegationStatuses.Accepted or DelegationStatuses.Rejected
                or DelegationStatuses.Failed or DelegationStatuses.TimedOut or DelegationStatuses.Cancelled;
            var archive = _tasks.Where(t => Finished(t) && t.UpdatedAt < cutoff).ToHashSet();
            var overflow = _tasks.Count - archive.Count - Math.Max(100, maxTasks);
            if (overflow > 0)
            {
                foreach (var t in _tasks.Where(t => Finished(t) && !archive.Contains(t)).OrderBy(t => t.UpdatedAt).Take(overflow))
                    archive.Add(t);
            }
            if (archive.Count == 0) return 0;

            var remaining = _tasks.Where(t => !archive.Contains(t)).ToList();
            await _persistence.ArchiveTasksAsync(archive, remaining);
            _tasks = remaining;
            return archive.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SubAgentBinding?> GetBindingAsync(string parentAgentId, string childAgentId)
    {
        await _lock.WaitAsync();
        try
        {
            return _bindings.FirstOrDefault(b => b.ParentAgentId == parentAgentId && b.ChildAgentId == childAgentId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SubAgentBinding> UpsertBindingAsync(SubAgentBinding binding)
    {
        await _lock.WaitAsync();
        try
        {
            var next = _bindings.Where(b => !(b.ParentAgentId == binding.ParentAgentId && b.ChildAgentId == binding.ChildAgentId)).Append(binding).ToList();
            await _persistence.UpsertBindingAsync(binding, next);
            _bindings = next;
            return binding;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteBindingAsync(string parentAgentId, string childAgentId)
    {
        await _lock.WaitAsync();
        try
        {
            var next = _bindings.Where(b => !(b.ParentAgentId == parentAgentId && b.ChildAgentId == childAgentId)).ToList();
            if (next.Count == _bindings.Count) return false;
            await _persistence.DeleteBindingAsync(parentAgentId, childAgentId, next);
            _bindings = next;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DelegationTask?> GetTaskAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            return _tasks.FirstOrDefault(t => t.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DelegationTask>> GetTasksAsync(string? parentAgentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            return _tasks.Where(t => parentAgentId is null || t.ParentAgentId == parentAgentId)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DelegationTask?> FindByNativeKeyAsync(string parentAgentId, string nativeKey)
    {
        await _lock.WaitAsync();
        try
        {
            return _tasks.FirstOrDefault(t => t.ParentAgentId == parentAgentId
                && (t.NativeTaskId == nativeKey || t.SessionId == nativeKey || t.Evidence.Contains($"call_id:{nativeKey}")));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DelegationTask> SaveTaskAsync(DelegationTask task)
    {
        await _lock.WaitAsync();
        try
        {
            var next = _tasks.Where(t => t.Id != task.Id).Append(task).ToList();
            await _persistence.SaveTaskAsync(task, next);
            _tasks = next;
            return task;
        }
        finally
        {
            _lock.Release();
        }
    }
}
