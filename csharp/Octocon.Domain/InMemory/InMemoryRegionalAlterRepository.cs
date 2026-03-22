using System.Collections.Concurrent;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Alters;
using Octocon.Domain.Friendships;
using Octocon.Domain.Settings;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionalAlterRepository : IAlterRepository
{
    private sealed class AlterState
    {
        public required int AlterId { get; init; }
        public string? Alias { get; set; }
        public string Name { get; set; } = string.Empty;
        public VisibilityLevel VisibilityLevel { get; set; } = VisibilityLevel.Public;
        public Dictionary<string, string?> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, AlterState>> _bySystem = new();
    private readonly ConcurrentDictionary<string, int> _nextIdBySystem = new();
    private readonly IFriendshipRepository? _friendships;
    private readonly ISettingsFieldRepository? _settingsFields;

    public InMemoryRegionalAlterRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public InMemoryRegionalAlterRepository(IRegionContext regionContext, IFriendshipRepository friendships)
    {
        _regionContext = regionContext;
        _friendships = friendships;
    }

    public InMemoryRegionalAlterRepository(IRegionContext regionContext, IFriendshipRepository friendships, ISettingsFieldRepository settingsFields)
    {
        _regionContext = regionContext;
        _friendships = friendships;
        _settingsFields = settingsFields;
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

        if (!string.IsNullOrWhiteSpace(command.SecurityLevel))
        {
            existing.VisibilityLevel = ParseVisibilityLevel(command.SecurityLevel);
        }

        if (command.Fields is not null)
        {
            foreach (var field in command.Fields)
            {
                existing.Fields[field.Id] = field.Value;
            }
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(store.TryRemove(alterId, out _));
    }

    public Task<IReadOnlyList<AlterReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Task.FromResult<IReadOnlyList<AlterReadModel>>(Array.Empty<AlterReadModel>());
        }

        var rows = store.Values
            .OrderBy(x => x.AlterId)
            .Select(x => new AlterReadModel(
                x.AlterId, 
                x.Name, 
                x.Alias,
                null,
                null,
                null,
                x.VisibilityLevel,
                null,
                null,
                null,
                null,
                null,
                null))
            .ToArray();

        return Task.FromResult<IReadOnlyList<AlterReadModel>>(rows);
    }

    public async Task<IReadOnlyList<AlterPublicReadModel>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
        var systemKey = GetSystemKey(systemId);
        var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Array.Empty<AlterPublicReadModel>();
        }

        var rows = store.Values
            .Where(x => CanView(friendshipLevel, x.VisibilityLevel))
            .OrderBy(x => x.AlterId)
            .Select(x => new AlterPublicReadModel(
                x.AlterId,
                x.Name,
                x.Alias,
                ResolveGuardedFields(x, definitions)))
            .ToArray();

        return rows;
    }

    public Task<AlterReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(alterId, out var alter))
        {
            return Task.FromResult<AlterReadModel?>(null);
        }

        return Task.FromResult<AlterReadModel?>(new AlterReadModel(
                alter.AlterId, 
                alter.Name, 
                alter.Alias,
                null,
                null,
                null,
                alter.VisibilityLevel,
                null,
                null,
                null,
                null,
                null,
                null));
    }

    public async Task<AlterPublicReadModel?> GetGuardedAsync(
        string systemId,
        int alterId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(alterId, out var alter))
        {
            return null;
        }

        if (!CanView(friendshipLevel, alter.VisibilityLevel))
        {
            return null;
        }

        return new AlterPublicReadModel(
            alter.AlterId,
            alter.Name,
            alter.Alias,
            ResolveGuardedFields(alter, await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken)));
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