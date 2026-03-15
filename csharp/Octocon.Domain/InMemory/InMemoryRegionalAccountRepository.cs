using System.Collections.Concurrent;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionalAccountRepository : IAccountRepository
{
    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, string> _usernameBySystem = new();
    private readonly ConcurrentDictionary<string, string> _descriptionBySystem = new();

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

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
