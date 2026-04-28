using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

public interface IScyllaKeyspaceResolver
{
    string ResolveRegionalKeyspace(string systemId);
    string ResolveGlobalKeyspace();
    string NormalizeSystemId(string systemId);
}

public sealed class ScyllaKeyspaceResolver : IScyllaKeyspaceResolver
{
    private readonly IRegionContext _regionContext;

    public ScyllaKeyspaceResolver(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public string ResolveRegionalKeyspace(string systemId)
    {
        var explicitRegion = ExtractRegionPrefix(systemId);
        if (explicitRegion is not null and not ("id" or "username" or "discord"))
        {
            return explicitRegion;
        }

        return _regionContext.ResolveUserRegion(systemId);
    }

    public string ResolveGlobalKeyspace() => "global";

    public string NormalizeSystemId(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return systemId;
        }

        var separator = systemId.IndexOf(':');
        if (separator <= 0 || separator >= systemId.Length - 1)
        {
            return systemId;
        }

        return systemId[(separator + 1)..];
    }

    private static string? ExtractRegionPrefix(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return null;
        }

        var separator = systemId.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        return systemId[..separator].ToLowerInvariant();
    }
}
