using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Configuration;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// Unified resolution for Scylla connection values.
/// Credentials and topology (contact_points, datacenter, username, password) come
/// exclusively from ISecretsStore. Only keyspace supports an IConfiguration override
/// (OCTOCON_SCYLLA_KEYSPACE env var) for per-instance identity.
/// </summary>
public static class ScyllaConfigResolver
{
    public static async Task<string[]> GetContactPointsAsync(
        ISecretsStore secretsStore, CancellationToken ct = default)
    {
        var raw = await secretsStore.GetAsync("scylla:contact_points", ct);
        return string.IsNullOrWhiteSpace(raw)
            ? ["127.0.0.1"]
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static async Task<string> GetDatacenterAsync(
        ISecretsStore secretsStore, CancellationToken ct = default)
    {
        return await secretsStore.GetAsync("scylla:local_datacenter", ct)
            ?? "datacenter1";
    }

    public static async Task<string?> GetUsernameAsync(
        ISecretsStore secretsStore, CancellationToken ct = default)
    {
        return await secretsStore.GetAsync("scylla:username", ct);
    }

    public static async Task<string> GetPasswordAsync(
        ISecretsStore secretsStore, CancellationToken ct = default)
    {
        return await secretsStore.GetAsync("scylla:password", ct)
            ?? string.Empty;
    }

    public static async Task<string> GetKeyspaceAsync(
        IConfiguration configuration, ISecretsStore secretsStore, CancellationToken ct = default)
    {
        return configuration["OCTOCON_SCYLLA_KEYSPACE"]
            ?? await secretsStore.GetAsync("scylla:keyspace", ct)
            ?? "nam";
    }
}
