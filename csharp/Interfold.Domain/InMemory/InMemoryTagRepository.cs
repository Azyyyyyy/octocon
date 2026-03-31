using System.Collections.Concurrent;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Alters;
using Interfold.Domain.Friendships;
using Interfold.Domain.Tags;

namespace Interfold.Domain.InMemory;

public sealed class InMemoryTagRepository : ITagRepository
{
   private sealed record TagState(
       string TagId,
       string? ParentTagId,
       string Name,
       DateTime InsertedAt,
       DateTime UpdatedAt,
       string? Color = null,
       string? Description = null,
       string? SecurityLevel = null
   );

    private readonly IRegionContext _regionContext;
    private readonly IFriendshipRepository? _friendships;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TagState>> _bySystem = new();
   // Key: "{systemKey}:{tagId}"  Value: set of alter IDs (dict used as concurrent set)
   private readonly ConcurrentDictionary<string, ConcurrentDictionary<BareAlter, bool>> _alterMemberships = new();

    public InMemoryTagRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public InMemoryTagRepository(IRegionContext regionContext, IFriendshipRepository friendships)
    {
        _regionContext = regionContext;
        _friendships = friendships;
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
        var now = DateTime.UtcNow;
        store[id] = new TagState(id, command.ParentTagId, command.Name, now, now);
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
               UpdatedAt   = DateTime.UtcNow,
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
           var members = _alterMemberships.GetOrAdd(memberKey, _ => new ConcurrentDictionary<BareAlter, bool>());
           members[new BareAlter(alterId, "", null, null, null, null, null!)] = true;
           return Task.FromResult(true);
       }

       public Task<bool> DetachAlterAsync(string systemId, string tagId, int alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           var memberKey = $"{systemKey}:{tagId}";
           if (!_alterMemberships.TryGetValue(memberKey, out var members))
               return Task.FromResult(false);

           return Task.FromResult(members.Remove(members.FirstOrDefault(x => x.Key.Id == alterId).Key, out _));
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

           store[tagId] = existing with { ParentTagId = parentTagId, UpdatedAt = DateTime.UtcNow };
           return Task.FromResult(true);
       }

       public Task<bool> RemoveParentAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var existing))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = null, UpdatedAt = DateTime.UtcNow };
           return Task.FromResult(true);
       }

       public Task<IReadOnlyList<TagReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Task.FromResult<IReadOnlyList<TagReadModel>>(Array.Empty<TagReadModel>());

           var rows = store.Values
               .OrderBy(x => x.TagId)
               .Select(x => new TagReadModel(
                   x.TagId,
                   x.Name,
                   x.Color,
                   x.Description,
                   x.ParentTagId,
                   GetAlterIds(systemKey, x.TagId),
                   x.InsertedAt,
                   x.UpdatedAt,
                   ParseVisibilityLevel(x.SecurityLevel),
                   systemId))
               .ToArray();

           return Task.FromResult<IReadOnlyList<TagReadModel>>(rows);
       }

       public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
           string systemId,
           string? viewerSystemId,
           CancellationToken cancellationToken = default)
       {
           var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Array.Empty<TagPublicReadModel>();

           var rows = store.Values
               .Where(x => CanView(friendshipLevel, ParseVisibilityLevel(x.SecurityLevel)))
               .OrderBy(x => x.TagId)
               .Select(x => new TagPublicReadModel(
                   x.TagId,
                   x.Name,
                   x.Color,
                   x.Description,
                   x.ParentTagId,
                   GetAlters(systemKey, x.TagId),
                   x.InsertedAt,
                   x.UpdatedAt,
                   ParseVisibilityLevel(x.SecurityLevel),
                   systemId))
               .ToArray();

           return rows;
       }

       public Task<TagReadModel?> GetAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var tag))
               return Task.FromResult<TagReadModel?>(null);

           return Task.FromResult<TagReadModel?>(new TagReadModel(
               tag.TagId,
               tag.Name,
               tag.Color,
               tag.Description,
               tag.ParentTagId,
               GetAlterIds(systemKey, tag.TagId),
               tag.InsertedAt,
               tag.UpdatedAt,
               ParseVisibilityLevel(tag.SecurityLevel),
               systemId));
       }

       public async Task<TagPublicReadModel?> GetGuardedAsync(
           string systemId,
           string tagId,
           string? viewerSystemId,
           CancellationToken cancellationToken = default)
       {
           var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var tag))
           {
               return null;
           }

           if (!CanView(friendshipLevel, ParseVisibilityLevel(tag.SecurityLevel)))
           {
               return null;
           }

           return new TagPublicReadModel(
               tag.TagId,
               tag.Name,
               tag.Color,
               tag.Description,
               tag.ParentTagId,
               GetAlters(systemKey, tag.TagId),
               tag.InsertedAt,
               tag.UpdatedAt,
               ParseVisibilityLevel(tag.SecurityLevel),
               systemId);
       }

    private IReadOnlyList<int> GetAlterIds(string systemKey, string tagId)
    {
        var memberKey = $"{systemKey}:{tagId}";
        if (!_alterMemberships.TryGetValue(memberKey, out var members))
            return Array.Empty<int>();

        return members.Keys.OrderBy(x => x.Id).Select(x => x.Id).ToArray();
    }

    private IReadOnlyList<BareAlter> GetAlters(string systemKey, string tagId)
    {
        var memberKey = $"{systemKey}:{tagId}";
        if (!_alterMemberships.TryGetValue(memberKey, out var members))
            return Array.Empty<BareAlter>();

        return members.Keys.OrderBy(x => x.Id).ToArray();
    }

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }

    private async Task<string?> ResolveFriendshipLevelAsync(string systemId, string? viewerSystemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId))
        {
            return null;
        }

        if (string.Equals(systemId, viewerSystemId, StringComparison.Ordinal))
        {
            return "trusted_friend";
        }

        if (_friendships is null)
        {
            return null;
        }

        return await _friendships.GetFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
    }

    private static bool CanView(string? friendshipLevel, VisibilityLevel visibilityLevel)
    {
        return visibilityLevel switch
        {
            VisibilityLevel.Public => true,
            VisibilityLevel.FriendsOnly => friendshipLevel is "friend" or "trusted_friend",
            VisibilityLevel.TrustedOnly => friendshipLevel is "trusted_friend",
            _ => false
        };
    }

    private static VisibilityLevel ParseVisibilityLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "friends_only" => VisibilityLevel.FriendsOnly,
            "trusted_only" => VisibilityLevel.TrustedOnly,
            "private" => VisibilityLevel.Private,
            _ => VisibilityLevel.Public
        };
    }
}
