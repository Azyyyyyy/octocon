using System.Collections.Concurrent;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryAlterRepository : IAlterRepository
{
    private sealed class AlterState
    {
        public required int AlterId { get; init; }
        public string? Alias { get; set; }
        public string? AvatarUrl { get; set; }
        public AvatarSource? AvatarSource { get; set; }
        public string? Description { get; set; }
        public string? Color { get; set; }
        public string? Pronouns { get; set; }
        public string? ProxyName { get; set; }
        public string Name { get; set; } = string.Empty;
        public VisibilityLevel VisibilityLevel { get; set; } = VisibilityLevel.Public;
        public Dictionary<string, string?> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Untracked { get; set; }
        public bool Archived { get; set; }
        public bool Pinned { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, AlterState>> _bySystem = new();
    private readonly ConcurrentDictionary<string, int> _nextIdBySystem = new();
    private readonly IFriendshipRepository? _friendships;
    private readonly ISettingsFieldRepository? _settingsFields;
    private readonly IPollRepository? _polls;

    public InMemoryAlterRepository(
        IRegionContext regionContext,
        IFriendshipRepository friendships,
        ISettingsFieldRepository settingsFields,
        IPollRepository polls)
    {
        _regionContext = regionContext;
        _friendships = friendships;
        _settingsFields = settingsFields;
        _polls = polls;
    }

    public Task<int?> CreateAsync(
        string systemId,
        CreateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<int, AlterState>());
        var next = _nextIdBySystem.AddOrUpdate(systemKey, 1, (_, current) => current + 1);

        var created = store.TryAdd(next, new AlterState
        {
            AlterId = next,
            Name = command.Name
        });

        return Task.FromResult<int?>(created ? next : null);
    }

    public Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(alterId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(
        string systemId,
        UpdateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.AlterId, out var existing))
        {
            return Task.FromResult(false);
        }

        if (!string.IsNullOrWhiteSpace(command.Alias))
        {
            existing.Alias = command.Alias;
        }

        if (!string.IsNullOrWhiteSpace(command.Name))
        {
            existing.Name = command.Name;
        }

        if (command.Description is not null)
        {
            existing.Description = command.Description;
        }

        if (command.Color is not null)
        {
            existing.Color = command.Color;
        }

        if (command.Pronouns is not null)
        {
            existing.Pronouns = command.Pronouns;
        }

        if (command.ProxyName is not null)
        {
            existing.ProxyName = command.ProxyName;
        }

        if (!string.IsNullOrWhiteSpace(command.SecurityLevel))
        {
            existing.VisibilityLevel = ParseVisibilityLevel(command.SecurityLevel);
        }

        if (command.ClearAvatar)
        {
            existing.AvatarUrl = null;
            existing.AvatarSource = null;
        }
        else if (command.AvatarUrl is not null)
        {
            // avatar_url and avatar_source must move together; the domain handler
            // rejects the half-set case so we treat AvatarSource as required here.
            existing.AvatarUrl = command.AvatarUrl;
            existing.AvatarSource = command.AvatarSource ?? AvatarSource.Local;
        }

        if (command.Fields is not null)
        {
            foreach (var field in command.Fields)
            {
                existing.Fields[field.Id] = field.Value;
            }
        }

        if (command.Untracked is not null)
        {
            existing.Untracked = command.Untracked.Value;
        }

        if (command.Archived is not null)
        {
            existing.Archived = command.Archived.Value;
        }

        if (command.Pinned is not null)
        {
            existing.Pinned = command.Pinned.Value;
        }

        return Task.FromResult(true);
    }

    public async Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return false;
        }

        var removed = store.TryRemove(alterId, out _);
        if (removed && _polls != null)
        {
            await _polls.RemoveAlterFromPollsAsync(systemId, alterId, cancellationToken);
        }

        return removed;
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Array.Empty<AlterReadModel>();
        }

        // Owner can be treated as trusted_friend for visibility resolution
        var definitions = await ResolveVisibleDefinitionsAsync(systemId, "trusted_friend", cancellationToken);

        var rows = store.Values
            .OrderBy(x => x.AlterId)
            .Select(x => new AlterReadModel(
                x.AlterId,
                x.Name,
                x.Description,
                x.AvatarUrl,
                x.AvatarSource,
                x.Color,
                x.Pronouns,
                x.VisibilityLevel,
                ResolveGuardedFields(x, definitions),
                x.ProxyName,
                x.Alias,
                x.Untracked,
                x.Archived,
                x.Pinned))
            .ToArray();

        return rows;
    }

    public async Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
        var systemKey = GetSystemKey(systemId);
        var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Array.Empty<BareAlter>();
        }

        var rows = store.Values
            .Where(x => CanView(friendshipLevel, x.VisibilityLevel))
            .OrderBy(x => x.AlterId)
            .Select(x => new BareAlter(
                x.AlterId,
                x.Name,
                x.AvatarUrl,
                x.AvatarSource,
                x.Color,
                x.Pronouns,
                x.Description,
                ResolveGuardedFields(x, definitions)))
            .ToArray();

        return rows;
    }

    public async Task<AlterReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(alterId, out var alter))
        {
            return null;
        }

        var definitions = await ResolveVisibleDefinitionsAsync(systemId, "trusted_friend", cancellationToken);

        return new AlterReadModel(
            alter.AlterId,
            alter.Name,
            alter.Description,
            alter.AvatarUrl,
            alter.AvatarSource,
            alter.Color,
            alter.Pronouns,
            alter.VisibilityLevel,
            ResolveGuardedFields(alter, definitions),
            alter.ProxyName,
            alter.Alias,
            alter.Untracked,
            alter.Archived,
            alter.Pinned);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        string systemId,
        int alterId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
        var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(alterId, out var alter))
        {
            return null;
        }

        if (!CanView(friendshipLevel, alter.VisibilityLevel))
        {
            return null;
        }

        return new BareAlter(
            alter.AlterId,
            alter.Name,
            alter.AvatarUrl,
            alter.AvatarSource,
            alter.Color,
            alter.Pronouns,
            alter.Description,
            ResolveGuardedFields(alter, definitions));
    }

    public Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Task.FromResult(false);
        }

        var taken = store.Values.Any(a =>
            a.AlterId != alterId &&
            !string.IsNullOrWhiteSpace(a.Alias) &&
            string.Equals(a.Alias, alias, StringComparison.OrdinalIgnoreCase)
        );

        return Task.FromResult(taken);
    }

    internal void RemoveFieldValuesForSystem(Guid fieldId, string systemKey)
    {
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return;

        var fieldKey = fieldId.ToString("N");
        foreach (var kv in store)
        {
            var state = kv.Value;
            lock (state.Fields)
            {
                if (state.Fields.ContainsKey(fieldKey))
                {
                    state.Fields.Remove(fieldKey);
                }
            }
        }
    }

    internal void RemoveFieldValuesForSystem(string systemId, Guid fieldId)
    {
        var systemKey = GetSystemKey(systemId);
        RemoveFieldValuesForSystem(fieldId, systemKey);
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

    private static VisibilityLevel ParseVisibilityLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "friends_only" => VisibilityLevel.FriendsOnly,
            "trusted_only" => VisibilityLevel.TrustedOnly,
            "private" => VisibilityLevel.Private,
            _ => VisibilityLevel.Public
        };
    }

    private IReadOnlyList<AlterPublicFieldReadModel> ResolveGuardedFields(
        AlterState alter,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (alter.Fields.Count == 0 || definitions.Count == 0)
        {
            return Array.Empty<AlterPublicFieldReadModel>();
        }

        return definitions
            .Where(def => alter.Fields.ContainsKey(def.Id))
            .Select(def => new AlterPublicFieldReadModel(
                def.Id,
                def.Name,
                def.Type,
                alter.Fields.TryGetValue(def.Id, out var value) ? value : null))
            .ToArray();
    }

    private async Task<IReadOnlyList<SettingsFieldReadModel>> ResolveVisibleDefinitionsAsync(
        string systemId,
        string? friendshipLevel,
        CancellationToken cancellationToken)
    {
        if (_settingsFields is null)
        {
            return Array.Empty<SettingsFieldReadModel>();
        }

        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
        return definitions
            .Where(def => CanView(friendshipLevel, ParseVisibilityLevel(def.SecurityLevel)))
            .ToArray();
    }
}