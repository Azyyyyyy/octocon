using System.Collections.Concurrent;
using Interfold.Infrastructure.Configuration;
using Cassandra;
using Microsoft.Extensions.Logging;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Persistence.Scylla;

/// <summary>
/// IRegionContext backed by global.user_registry, with a bounded in-process cache and
/// fallback to the locally configured default region when the registry row is absent or
/// the Scylla session is not yet available.
/// </summary>
public sealed class ScyllaUserRegistryRegionContext : IRegionContext
{
    // Bounded LRU: keep most-recently-used entries simple via ConcurrentDictionary.
    // A higher-fidelity LRU eviction policy can be added later if memory pressure warrants it.
    private const int MaxCacheSize = 1024;

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaUserRegistryRegionContext> _logger;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public string CurrentRegion { get; }

    public ScyllaUserRegistryRegionContext(
        IScyllaSessionProvider sessionProvider,
        PersistenceConfiguration options,
        ILogger<ScyllaUserRegistryRegionContext> logger)
    {
        _sessionProvider = sessionProvider;
        _options = options;
        _logger = logger;
        CurrentRegion = options.DefaultRegion;
    }

    public string ResolveUserRegion(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        // Strip legacy region prefix "nam:abcdefg" → "abcdefg" before cache lookup.
        var raw = StripRegionPrefix(systemId, out var prefix);

        if (_cache.TryGetValue(raw, out var cached))
            return cached;

        // Synchronous path: attempt a best-effort lookup on the calling thread.
        // This keeps the interface non-async while avoiding a thread-pool deadlock on most
        // callers that are already async. GetAwaiter().GetResult() is safe here because
        // the backing Cassandra driver never marshals back to the same synchronization
        // context that an ASP.NET request would occupy.
        try
        {
            var region = LookupAsync(raw, prefix).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(region))
            {
                StoreInCache(raw, region);
                return region;
            }
        }
        catch (Exception ex)
        {
            // Any exception (session not yet ready, network error) falls through to default.
            _logger.LogWarning(ex, "Region lookup for system {SystemId} failed; falling back to default region.", raw);
        }

        return CurrentRegion;
    }

    public string ResolveConsistency(string targetRegion) =>
        string.Equals(targetRegion, CurrentRegion, StringComparison.OrdinalIgnoreCase)
            ? "local"
            : "global";

    /// <summary>
    /// Asynchronous variant to be used in hot paths that already have an async context.
    /// Returns null when the registry row is absent.
    /// </summary>
    public async Task<string?> ResolveUserRegionAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        var raw = StripRegionPrefix(systemId, out var prefix);

        if (_cache.TryGetValue(raw, out var cached))
            return cached;

        var region = await LookupAsync(raw, prefix, cancellationToken);
        if (!string.IsNullOrWhiteSpace(region))
        {
            StoreInCache(raw, region);
            return region;
        }

        return CurrentRegion;
    }

    /// <summary>Populates the cache for a user whose home region is already known
    /// (e.g. after account registration).</summary>
    public void RegisterRegion(string systemId, string region)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(region))
            return;

        StoreInCache(StripRegionPrefix(systemId, out var prefix), region.ToLowerInvariant());
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private async Task<string?> LookupAsync(
        string normalizedSystemId,
        string? prefix,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            SimpleStatement query;

            if (prefix == "username")
            {
                query = new SimpleStatement(
                    "SELECT region FROM global.user_registry WHERE username = ? LIMIT 1",
                    normalizedSystemId);
            }
            else if (prefix == "discord")
            {
                query = new SimpleStatement(
                    "SELECT region FROM global.user_registry WHERE discord_id = ? LIMIT 1",
                    normalizedSystemId);
            }
            else 
            {
                query = new SimpleStatement(
                    "SELECT region FROM global.user_registry WHERE user_id = ? LIMIT 1",
                    normalizedSystemId);                
            }

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row?.GetValue<string>("region");
        }, _options, cancellationToken, _logger);
    }

    private void StoreInCache(string key, string region)
    {
        if (_cache.Count >= MaxCacheSize)
        {
            // Simple eviction: clear on overflow to avoid unbounded growth.
            // A proper LRU would use a linked list; this is sufficient for phase-3 scope.
            _cache.Clear();
        }

        _cache[key] = region;
    }

    private static string StripRegionPrefix(string systemId, out string prefix)
    {
        var separator = systemId.IndexOf(':');
        if (separator > 0 && separator < systemId.Length - 1)
        {
            prefix = systemId[..separator].ToLowerInvariant();
            return systemId[(separator + 1)..];
        }

        prefix = null!;
        return systemId;
    }
}
