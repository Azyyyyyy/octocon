using System.Collections.Concurrent;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Tags;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryTagRepository : ITagRepository
{
   private sealed record TagState(
       string TagId,
       string? ParentTagId,
       string Name,
       string? Color = null,
       string? Description = null,
       string? SecurityLevel = null
   );

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TagState>> _bySystem = new();
   // Key: "{systemKey}:{tagId}"  Value: set of alter IDs (dict used as concurrent set)
   private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, bool>> _alterMemberships = new();

    public InMemoryTagRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<string?> CreateAsync(
        string systemId,
        CreateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<string, TagState>());

        if (!string.IsNullOrWhiteSpace(command.ParentTagId) && !store.ContainsKey(command.ParentTagId))
            return Task.FromResult<string?>(null);

        var id = Guid.NewGuid().ToString("N");
        store[id] = new TagState(id, command.ParentTagId, command.Name);
        return Task.FromResult<string?>(id);
    }

    public Task<bool> ExistsAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(tagId);
        return Task.FromResult(exists);
    }

       public Task<bool> UpdateAsync(string systemId, UpdateTagCommand command, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.TagId, out var existing))
               return Task.FromResult(false);

           store[command.TagId] = existing with
           {
               Name        = command.Name        ?? existing.Name,
               Color       = command.Color       ?? existing.Color,
               Description = command.Description ?? existing.Description,
               SecurityLevel = command.SecurityLevel ?? existing.SecurityLevel
           };
           return Task.FromResult(true);
       }

       public Task<bool> DeleteAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Task.FromResult(false);

           var removed = store.TryRemove(tagId, out _);
           if (removed)
               _alterMemberships.TryRemove($"{systemKey}:{tagId}", out _);
           return Task.FromResult(removed);
       }

       public Task<bool> AttachAlterAsync(string systemId, string tagId, int alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.ContainsKey(tagId))
               return Task.FromResult(false);

           var memberKey = $"{systemKey}:{tagId}";
           var members = _alterMemberships.GetOrAdd(memberKey, _ => new ConcurrentDictionary<int, bool>());
           members[alterId] = true;
           return Task.FromResult(true);
       }

       public Task<bool> DetachAlterAsync(string systemId, string tagId, int alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           var memberKey = $"{systemKey}:{tagId}";
           if (!_alterMemberships.TryGetValue(memberKey, out var members))
               return Task.FromResult(false);

           return Task.FromResult(members.TryRemove(alterId, out _));
       }

       public Task<string?> GetParentIdAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (_bySystem.TryGetValue(systemKey, out var store) && store.TryGetValue(tagId, out var state))
               return Task.FromResult(state.ParentTagId);

           return Task.FromResult<string?>(null);
       }

       public Task<bool> SetParentAsync(string systemId, string tagId, string parentTagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Task.FromResult(false);

           if (!store.TryGetValue(tagId, out var existing) || !store.ContainsKey(parentTagId))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = parentTagId };
           return Task.FromResult(true);
       }

       public Task<bool> RemoveParentAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var existing))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = null };
           return Task.FromResult(true);
       }

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
