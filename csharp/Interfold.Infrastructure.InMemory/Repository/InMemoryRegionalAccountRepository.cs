using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryRegionalAccountRepository : IAccountRepository
{
    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, string> _usernameBySystem = new();
    private readonly ConcurrentDictionary<string, string> _descriptionBySystem = new();
    private readonly ConcurrentDictionary<string, string> _avatarBySystem = new();
    private readonly ConcurrentDictionary<string, string> _linkTokenBySystem = new();
    private readonly ConcurrentDictionary<string, string> _systemByLinkToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _discordBySystem = new();
    private readonly ConcurrentDictionary<string, string> _emailBySystem = new();
    private readonly ConcurrentDictionary<string, string> _appleBySystem = new();
    private readonly ConcurrentDictionary<string, string> _systemByDiscord = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _systemByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _systemByApple = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRegionalAccountRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<bool> UpdateUsernameAsync(string systemId, string username, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _usernameBySystem[systemKey] = username;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateDescriptionAsync(string systemId, string description, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _descriptionBySystem[systemKey] = description;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateAvatarAsync(string systemId, string avatarUrl, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _avatarBySystem[systemKey] = avatarUrl;
        return Task.FromResult(true);
    }

    public Task<bool> ClearAvatarAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _avatarBySystem.TryRemove(systemKey, out _);
        return Task.FromResult(true);
    }

    public Task<string> GetOrCreateLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var token = _linkTokenBySystem.GetOrAdd(systemKey, static key =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        });

        _systemByLinkToken[token] = _regionContext.ResolveUserRegion(systemId) + ":" + systemId;

        return Task.FromResult(token);
    }

    public Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _linkTokenBySystem.TryGetValue(systemKey, out var token);
        return Task.FromResult(token);
    }

    public Task<string?> ResolveSystemIdByLinkTokenAsync(string linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken))
        {
            return Task.FromResult<string?>(null);
        }

        _systemByLinkToken.TryGetValue(linkToken, out var scopedSystemId);
        return Task.FromResult(scopedSystemId);
    }

    public Task<bool> ClearLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }

        return Task.FromResult(true);
    }

    public Task<string?> FindSystemIdByDiscordIdAsync(string discordId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discordId))
        {
            return Task.FromResult<string?>(null);
        }

        if (_systemByDiscord.TryGetValue(discordId, out var scopedSystemId))
        {
            return Task.FromResult<string?>(scopedSystemId);
        }

        // Auto-create new system
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = _regionContext.ResolveUserRegion(newSystemId) + ":" + newSystemId;
        _discordBySystem[newSystemId] = discordId;
        _systemByDiscord[discordId] = scopedNewSystemId;
        return Task.FromResult<string?>(scopedNewSystemId);
    }

    public Task<string?> FindSystemIdByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult<string?>(null);
        }

        if (_systemByEmail.TryGetValue(email, out var scopedSystemId))
        {
            return Task.FromResult<string?>(scopedSystemId);
        }

        // Auto-create new system
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = _regionContext.ResolveUserRegion(newSystemId) + ":" + newSystemId;
        _emailBySystem[newSystemId] = email;
        _systemByEmail[email] = scopedNewSystemId;
        return Task.FromResult<string?>(scopedNewSystemId);
    }

    public Task<string?> FindSystemIdByAppleIdAsync(string appleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return Task.FromResult<string?>(null);
        }

        if (_systemByApple.TryGetValue(appleId, out var scopedSystemId))
        {
            return Task.FromResult<string?>(scopedSystemId);
        }

        // Auto-create new system
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNewSystemId = _regionContext.ResolveUserRegion(newSystemId) + ":" + newSystemId;
        _appleBySystem[newSystemId] = appleId;
        _systemByApple[appleId] = scopedNewSystemId;
        return Task.FromResult<string?>(scopedNewSystemId);
    }

    public Task<AccountLinkResult> LinkDiscordToUserAsync(string systemId, string discordId, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, discordId, _discordBySystem, _systemByDiscord));

    public Task<AccountLinkResult> LinkEmailToUserAsync(string systemId, string email, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, email, _emailBySystem, _systemByEmail));

    public Task<AccountLinkResult> LinkAppleToUserAsync(string systemId, string appleId, CancellationToken cancellationToken = default)
        => Task.FromResult(LinkIdentifier(systemId, appleId, _appleBySystem, _systemByApple));

    public Task<bool> UnlinkDiscordAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId))
        {
            _systemByDiscord.TryRemove(discordId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkEmailAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email))
        {
            _systemByEmail.TryRemove(email, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkAppleAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId))
        {
            _systemByApple.TryRemove(appleId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        _usernameBySystem.TryRemove(systemKey, out _);
        _descriptionBySystem.TryRemove(systemKey, out _);
        _avatarBySystem.TryRemove(systemKey, out _);
        _linkTokenBySystem.TryRemove(systemKey, out _);

        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId))
        {
            _systemByDiscord.TryRemove(discordId, out _);
        }

        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email))
        {
            _systemByEmail.TryRemove(email, out _);
        }

        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId))
        {
            _systemByApple.TryRemove(appleId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var username = _usernameBySystem.TryGetValue(systemKey, out var u) ? u : null;
        var description = _descriptionBySystem.TryGetValue(systemKey, out var d) ? d : null;
        var avatarUrl = _avatarBySystem.TryGetValue(systemKey, out var a) ? a : null;
        var discordId = _discordBySystem.TryGetValue(systemKey, out var discord) ? discord : null;
        var email = _emailBySystem.TryGetValue(systemKey, out var e) ? e : null;
        var appleId = _appleBySystem.TryGetValue(systemKey, out var apple) ? apple : null;

        if (username is null && description is null && avatarUrl is null && discordId is null && email is null && appleId is null)
        {
            return Task.FromResult<AccountPublicProfileReadModel?>(null);
        }

        return Task.FromResult<AccountPublicProfileReadModel?>(
            new AccountPublicProfileReadModel(systemId, username, description, avatarUrl, discordId, email, appleId));
    }

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }

    private AccountLinkResult LinkIdentifier(
        string systemId,
        string identifier,
        ConcurrentDictionary<string, string> identifierBySystem,
        ConcurrentDictionary<string, string> systemByIdentifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return AccountLinkResult.UserNotFound;
        }

        var systemKey = GetSystemKey(systemId);
        var scopedSystemId = _regionContext.ResolveUserRegion(systemId) + ":" + systemId;

        if (_usernameBySystem.ContainsKey(systemKey) is false &&
            _descriptionBySystem.ContainsKey(systemKey) is false &&
            _avatarBySystem.ContainsKey(systemKey) is false &&
            _linkTokenBySystem.ContainsKey(systemKey) is false)
        {
            return AccountLinkResult.UserNotFound;
        }

        if (identifierBySystem.TryGetValue(systemKey, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return AccountLinkResult.AlreadyLinked;
        }

        if (systemByIdentifier.TryGetValue(identifier, out var owner) && !string.Equals(owner, scopedSystemId, StringComparison.Ordinal))
        {
            return AccountLinkResult.UserExists;
        }

        identifierBySystem[systemKey] = identifier;
        systemByIdentifier[identifier] = scopedSystemId;
        return AccountLinkResult.Success;
    }
}
