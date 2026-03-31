using Microsoft.Extensions.DependencyInjection;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;
using Interfold.Domain.InMemory;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings;
using Interfold.Domain.Tags;
using Interfold.Infrastructure.Persistence;
using Interfold.Infrastructure.Persistence.Bootstrap;
using Interfold.Infrastructure.Persistence.Postgres;
using Interfold.Infrastructure.Persistence.Scylla;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddInterfoldPersistence(
        this IServiceCollection services,
        PersistenceMode mode,
        Action<PersistenceRegistrationOptions>? configure = null
    )
    {
        var options = new PersistenceRegistrationOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        return mode switch
        {
            PersistenceMode.InMemory => services
                .AddSingleton<IRegionContext>(_ => new InMemoryRegionContext(options.DefaultRegion))
                .AddSingleton<IAccountRepository, InMemoryRegionalAccountRepository>()
                .AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>()
                .AddSingleton<IEncryptionStateRepository, InMemoryEncryptionStateRepository>()
                .AddSingleton<ISettingsFieldRepository, InMemorySettingsFieldRepository>()
                .AddSingleton<IAlterRepository, InMemoryRegionalAlterRepository>()
                .AddSingleton<IFrontingRepository, InMemoryRegionalFrontingRepository>()
                .AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>()
                .AddSingleton<ITagRepository, InMemoryTagRepository>()
                .AddSingleton<IJournalRepository, InMemoryJournalRepository>()
                .AddSingleton<IPollRepository, InMemoryPollRepository>()
                .AddSingleton<IAggregateVersionStore, InMemoryRegionalAggregateVersionStore>()
                .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
                .AddSingleton<IDatabaseBootstrapHealthChecker>(sp => 
                    new InMemoryBootstrapHealthChecker(sp.GetRequiredService<IAlterRepository>()))
                .AddSingleton<IOperationalHealthChecker>(sp =>
                    sp.GetRequiredService<IDatabaseBootstrapHealthChecker>() as IOperationalHealthChecker ?? throw new InvalidOperationException()),

            PersistenceMode.ScyllaPostgres => services
                .AddSingleton<IPostgresConnectionFactory>(_ => new PostgresConnectionFactory(options.PostgresConnectionString, options))
                .AddSingleton<IScyllaSessionProvider, ScyllaSessionProvider>()
                .AddSingleton<IScyllaKeyspaceResolver, ScyllaKeyspaceResolver>()
                .AddSingleton<IRegionContext, ScyllaUserRegistryRegionContext>()
                .AddSingleton<IAccountRepository, ScyllaAccountRepository>()
                .AddSingleton<INotificationTokenRepository, ScyllaNotificationTokenRepository>()
                .AddSingleton<IEncryptionStateRepository, ScyllaEncryptionStateRepository>()
                .AddSingleton<ISettingsFieldRepository, ScyllaSettingsFieldRepository>()
                .AddSingleton<IAlterRepository, ScyllaAlterRepository>()
                .AddSingleton<IFrontingRepository, ScyllaFrontingRepository>()
                .AddSingleton<IFriendshipRepository, ScyllaFriendshipRepository>()
                .AddSingleton<ITagRepository, ScyllaTagRepository>()
                .AddSingleton<IJournalRepository, ScyllaJournalRepository>()
                .AddSingleton<IPollRepository, ScyllaPollRepository>()
                .AddSingleton<IAggregateVersionStore, ScyllaAggregateVersionStore>()
                .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
                .AddSingleton<IDatabaseBootstrapHealthChecker>(sp =>
                    new ScyllaPostgresBootstrapHealthChecker(
                        sp.GetRequiredService<IPostgresConnectionFactory>(),
                        sp.GetRequiredService<IScyllaSessionProvider>(),
                        sp.GetRequiredService<IAlterRepository>(),
                        options))
                .AddSingleton<IOperationalHealthChecker>(sp =>
                    sp.GetRequiredService<IDatabaseBootstrapHealthChecker>() as IOperationalHealthChecker ?? throw new InvalidOperationException()),

            _ => throw new InvalidOperationException($"Unsupported persistence mode: {mode}")
        };
    }
}

