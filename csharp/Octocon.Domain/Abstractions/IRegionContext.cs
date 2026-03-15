namespace Octocon.Domain.Abstractions;

public interface IRegionContext
{
    string CurrentRegion { get; }
    string ResolveUserRegion(string systemId);
    string ResolveConsistency(string targetRegion);
}
