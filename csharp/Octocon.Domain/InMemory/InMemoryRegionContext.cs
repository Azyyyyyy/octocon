using Octocon.Domain.Abstractions;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionContext : IRegionContext
{
    private static readonly string[] Regions = ["nam", "eur", "ocn", "sam", "sas", "gdpr"];

    public string CurrentRegion { get; }

    public InMemoryRegionContext(string currentRegion = "nam")
    {
        CurrentRegion = currentRegion;
    }

    public string ResolveUserRegion(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return CurrentRegion;
        }

        var index = Math.Abs(systemId.GetHashCode(StringComparison.Ordinal)) % Regions.Length;
        return Regions[index];
    }

    public string ResolveConsistency(string targetRegion) =>
        string.Equals(targetRegion, CurrentRegion, StringComparison.OrdinalIgnoreCase)
            ? "local"
            : "global";
}