using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.InMemory;

public static class InMemoryServiceCollectionExtensions
{
    // Lazy<T> (default ExecutionAndPublication mode) is a wait-for-completion gate: exactly
    // one caller runs the factory while every other caller blocks until it returns. The
    // previous Interlocked.Exchange gate was fire-and-forget — the second caller would
    // observe `_added == 1` and return *before* the first caller had actually populated
    // PersistenceReg, then race ahead to AddInterfoldPersistence and trip the
    // "Persistence mode has not yet been implemented" throw. The integration suite hits
    // this when multiple WebApplicationFactory<Program> instances build their hosts in
    // parallel; replacing the gate with Lazy<bool> serialises observers behind the
    // registration so PersistenceReg is guaranteed populated by the time Register() returns.
    private static readonly Lazy<bool> Registration = new(() =>
    {
        ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.InMemory, AddInMemoryPersistence);
        return true;
    });

    public static void Register() => _ = Registration.Value;

    private static IServiceCollection AddInMemoryPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        return services
            .AddSingleton<IRegionContext>(_ => new InMemoryRegionContext(options.ScyllaKeyspace))
            // Seed the in-memory secrets store from `OCTOCON_INMEMORY_SECRETS_SEED__*`
            // configuration values so an external runner (Kotlin Testcontainers harness, ad-hoc
            // local container, etc.) can bootstrap the published image without an in-process
            // hook. We resolve via IConfiguration rather than Environment.GetEnvironmentVariable
            // so the same code path serves real env vars (EnvironmentVariablesConfigurationProvider)
            // *and* the test fixture's FactoryConfigurationProvider overrides — no global
            // env-state mutation needed in tests. Blank/missing values are skipped silently;
            // SecretsBootstrapService is the single source of fail-fast for encryption:pepper
            // and we deliberately don't duplicate that contract here.
            .AddSingleton<ISecretsStore>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var store = new InMemorySecretsStore();
                SeedFromConfig(store, config, "OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER",           "encryption:pepper");
                SeedFromConfig(store, config, "OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM",  "auth:jwt_es256_private_pem");
                SeedFromConfig(store, config, "OCTOCON_INMEMORY_SECRETS_SEED__AUTH_DEEP_LINK_SECRET",       "auth:deep_link_secret");
                SeedFromConfig(store, config, "OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM", "auth:jwt_rsa256_private_pem");
                return store;
            })
            .AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>()
            .AddSingleton<IEncryptionStateRepository, InMemoryEncryptionStateRepository>()
            .AddSingleton<IAccountRepository, InMemoryAccountRepository>()
            .AddSingleton<ISettingsFieldRepository, InMemorySettingsFieldRepository>()
            .AddSingleton<IPollRepository, InMemoryPollRepository>()
            .AddSingleton<IAlterRepository, InMemoryAlterRepository>()
            .AddSingleton<IFrontingRepository, InMemoryFrontingRepository>()
            .AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>()
            .AddSingleton<ITagRepository, InMemoryTagRepository>()
            .AddSingleton<IJournalRepository, InMemoryJournalRepository>()
            .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, InMemoryAuthTokenRevocationRepository>();
    }

    private static void SeedFromConfig(InMemorySecretsStore store, IConfiguration config, string envName, string secretKey)
    {
        var value = config[envName];
        if (!string.IsNullOrWhiteSpace(value))
            store.Seed(secretKey, value);
    }
}