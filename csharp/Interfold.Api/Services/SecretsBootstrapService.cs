using System.Security.Cryptography;
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
/// migration services from ISecretsStore — this service handles the auth/app material that
/// gets surfaced through IOptionsMonitor&lt;AuthenticationConfiguration&gt;:
///   - OAuth client secrets (google, discord, apple)
///   - Encryption pepper
///   - JWT RSA-2048 + ES256 signing material (private PEM; public half is derived in place)
///   - Deep-link HMAC signing secret
/// The leaf PFX password lives in the same store under `certs:leaf_pfx_password` but is
/// fetched by a one-shot loader in <c>Program.cs</c> before Kestrel binds — see that file
/// for the Postgres-direct read path that runs ahead of any hosted service.
/// </summary>
public sealed class SecretsBootstrapService(
    ISecretsStore secretsStore,
    IOptionsMonitor<AuthenticationConfiguration> authOptions,
    ILogger<SecretsBootstrapService> logger) : IHostedLifecycleService
{
    private static readonly (string Key, Action<AuthenticationConfiguration, string> Patch)[] AuthSecretMappings =
    [
        ("oauth:google:client_secret",  (auth, v) => auth.GoogleOAuthClientSecret = v),
        ("oauth:discord:client_secret", (auth, v) => auth.DiscordOAuthClientSecret = v),
        ("oauth:apple:client_secret",   (auth, v) => auth.AppleOAuthClientSecret = v),
        ("encryption:pepper",           (auth, v) => auth.EncryptionPepper = v),
        ("auth:deep_link_secret",       (auth, v) => auth.DeepLinkSecret = v),
        ("auth:jwt_rsa256_private_pem", PatchRsa256),
        ("auth:jwt_es256_private_pem",  PatchEs256),
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

        // EncryptionPepper has no env fallback (used to throw at DI binding from
        // OCTOCON_ENCRYPTION_PEPPER, now strictly store-resident), and the request-time
        // consumers (Setup/RecoverEncryptionCommandHandler, SimplyPluralImportService)
        // would otherwise blow up with an opaque NullReferenceException on the first
        // encryption operation. Fail fast and loudly here instead.
        if (string.IsNullOrWhiteSpace(auth.EncryptionPepper))
        {
            throw new InvalidOperationException(
                "[secrets-bootstrap] internal.secrets:encryption:pepper is missing or empty. " +
                "Run the bootstrapper to seed it, or insert the row manually before starting the API.");
        }

        logger.LogInformation("[secrets-bootstrap] Patched {Count} configuration value(s) from secrets store.", patches);
    }

    /// <summary>
    /// Assigns the RSA-2048 JWT private key (PEM, PKCS#8) and derives the SPKI public PEM
    /// in place. The public half is what <see cref="Controllers.SettingsController"/> hands
    /// out via the JWKS endpoint and what <see cref="Helpers.RecoveryCodeResolver"/> wraps
    /// for envelope encryption.
    /// </summary>
    private static void PatchRsa256(AuthenticationConfiguration auth, string privatePem)
    {
        auth.Rsa256PrivateKey = privatePem;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem.AsSpan());
        auth.Rsa256PublicKey = rsa.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// Assigns the ES256 JWT private key (PEM, SEC1) and seeds the verification array with
    /// the same PEM. <see cref="System.Security.Cryptography.ECDsa.ImportFromPem"/> reads
    /// the public half out of the private PEM at verify time, so a single row covers both
    /// signing and validation.
    /// </summary>
    private static void PatchEs256(AuthenticationConfiguration auth, string privatePem)
    {
        auth.JwtEs256PrivateKeyPem = privatePem;
        auth.JwtEs256VerificationKeyPems = [privatePem];
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
