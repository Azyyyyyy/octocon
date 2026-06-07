using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Services;

/// <summary>
/// Loads secrets from internal.secrets at startup and patches AuthenticationConfiguration.
/// Must be registered BEFORE migration services so it runs first in StartingAsync.
///
/// Admin credentials (postgres:admin_password, scylla:admin_*) are read directly by the
/// migration services from ISecretsStore — this service only handles auth/app secrets:
///   - OAuth client secrets
///   - Encryption pepper
/// </summary>
public sealed class SecretsBootstrapService(
    ISecretsStore secretsStore,
    IOptionsMonitor<AuthenticationConfiguration> authOptions,
    ILogger<SecretsBootstrapService> logger) : IHostedLifecycleService
{
    private static readonly (string Key, Action<AuthenticationConfiguration, string> Patch)[] AuthSecretMappings =
    [
        ("oauth:google:client_secret", (auth, v) => auth.GoogleOAuthClientSecret = v),
        ("oauth:discord:client_secret", (auth, v) => auth.DiscordOAuthClientSecret = v),
        ("oauth:apple:client_secret", (auth, v) => auth.AppleOAuthClientSecret = v),
        ("encryption:pepper", (auth, v) => auth.EncryptionPepper = v),
    ];

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[secrets-bootstrap] Loading secrets from store...");

        var auth = authOptions.CurrentValue;
        var patches = 0;

        foreach (var (key, patch) in AuthSecretMappings)
        {
            var value = await secretsStore.GetAsync(key, cancellationToken);

            if (!string.IsNullOrWhiteSpace(value))
            {
                patch(auth, value);
                patches++;
            }
        }

        logger.LogInformation("[secrets-bootstrap] Patched {Count} configuration value(s) from secrets store.", patches);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
