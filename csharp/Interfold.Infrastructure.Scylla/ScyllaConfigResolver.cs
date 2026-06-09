using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Configuration;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// Unified resolution for Scylla connection values.
/// Contact points, port, and keyspace support IConfiguration overrides
/// (OCTOCON_SCYLLA_CONTACT_POINTS, OCTOCON_SCYLLA_PORT, OCTOCON_SCYLLA_KEYSPACE)
/// for integration tests where the host port differs from the secrets store value.
/// Credentials (username, password, datacenter) come exclusively from ISecretsStore.
/// </summary>
public static class ScyllaConfigResolver
{
    public static async Task<string[]> GetContactPointsAsync(
        IConfiguration configuration, ISecretsStore secretsStore, CancellationToken ct = default)
    {
        var configValue = configuration["OCTOCON_SCYLLA_CONTACT_POINTS"];
        if (!string.IsNullOrWhiteSpace(configValue))
            return configValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    public static async Task<int> GetPortAsync(
        IConfiguration configuration, ISecretsStore secretsStore, CancellationToken ct = default)
    {
        var configValue = configuration["OCTOCON_SCYLLA_PORT"];
        if (int.TryParse(configValue, out var configPort))
            return configPort;

        var raw = await secretsStore.GetAsync("scylla:port", ct);
        return int.TryParse(raw, out var port) ? port : 9042;
    }
}
