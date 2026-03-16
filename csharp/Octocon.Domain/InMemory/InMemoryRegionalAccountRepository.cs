using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionalAccountRepository : IAccountRepository
{
    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, string> _usernameBySystem = new();
    private readonly ConcurrentDictionary<string, string> _descriptionBySystem = new();
    private readonly ConcurrentDictionary<string, string> _avatarBySystem = new();
    private readonly ConcurrentDictionary<string, string> _linkTokenBySystem = new();

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

        return Task.FromResult(token);
    }

    public Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _linkTokenBySystem.TryGetValue(systemKey, out var token);
        return Task.FromResult(token);
    }

    public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var username = _usernameBySystem.TryGetValue(systemKey, out var u) ? u : null;
        var description = _descriptionBySystem.TryGetValue(systemKey, out var d) ? d : null;
        var avatarUrl = _avatarBySystem.TryGetValue(systemKey, out var a) ? a : null;

        if (username is null && description is null && avatarUrl is null)
        {
            return Task.FromResult<AccountPublicProfileReadModel?>(null);
        }

        return Task.FromResult<AccountPublicProfileReadModel?>(
            new AccountPublicProfileReadModel(systemId, username, description, avatarUrl));
    }

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
