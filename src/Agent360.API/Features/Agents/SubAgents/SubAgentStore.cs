using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.SubAgents;

/// <summary>
/// Durable JSON-file store for SubAgentBinding registry and DelegationTask ledger.
/// Writes are serialized and atomic (temp file + replace).
/// </summary>
public sealed class SubAgentStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _bindingsPath;
    private readonly string _ledgerPath;
    private List<SubAgentBinding> _bindings;
    private List<DelegationTask> _tasks;

    /// <summary>In-process record of the last skill content fingerprint pushed per parent agent.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> SyncedSkillFingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SubAgentStore(IOptions<SubAgentOptions> options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.Value.StoragePath)
            ? options.Value.StoragePath
            : Path.Combine(env.ContentRootPath, options.Value.StoragePath);
        Directory.CreateDirectory(dir);
        _bindingsPath = Path.Combine(dir, "subagent_bindings.json");
        _ledgerPath = Path.Combine(dir, "delegation_tasks.json");
        _bindings = Load<SubAgentBinding>(_bindingsPath);
        _tasks = Load<DelegationTask>(_ledgerPath);
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
            _bindings.RemoveAll(b => b.ParentAgentId == binding.ParentAgentId && b.ChildAgentId == binding.ChildAgentId);
            _bindings.Add(binding);
            await SaveAsync(_bindingsPath, _bindings);
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
            var removed = _bindings.RemoveAll(b => b.ParentAgentId == parentAgentId && b.ChildAgentId == childAgentId) > 0;
            if (removed) await SaveAsync(_bindingsPath, _bindings);
            return removed;
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
            _tasks.RemoveAll(t => t.Id == task.Id);
            _tasks.Add(task);
            await SaveAsync(_ledgerPath, _tasks);
            return task;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<T> Load<T>(string path)
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

    private static async Task SaveAsync<T>(string path, List<T> items)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(items, JsonOpts));
        File.Move(tmp, path, true);
    }
}
