using Microsoft.Extensions.DependencyInjection;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Friendships;
using Octocon.Domain.Fronting;
using Octocon.Domain.InMemory;
using Octocon.Domain.Journals;
using Octocon.Domain.Polls;
using Octocon.Domain.Settings;
using Octocon.Domain.Tags;
using Octocon.Infrastructure.Persistence;
using Octocon.Infrastructure.Persistence.Bootstrap;
using Octocon.Infrastructure.Persistence.Postgres;
using Octocon.Infrastructure.Persistence.Scylla;

namespace Octocon.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoconPersistence(
        this IServiceCollection services,
        PersistenceMode mode,
        Action<PersistenceRegistrationOptions>? configure = null
    )
    {
        var options = new PersistenceRegistrationOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IRegionContext>(_ => new InMemoryRegionContext(options.DefaultRegion));

        return mode switch
        {
            PersistenceMode.InMemory => services
                .AddSingleton<IAccountRepository, InMemoryRegionalAccountRepository>()
                .AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>()
                .AddSingleton<IEncryptionStateRepository, InMemoryEncryptionStateRepository>()
                .AddSingleton<IAlterRepository, InMemoryRegionalAlterRepository>()
                .AddSingleton<IFrontingRepository, InMemoryRegionalFrontingRepository>()
                .AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>()
                .AddSingleton<ITagRepository, InMemoryTagRepository>()
                .AddSingleton<IJournalRepository, InMemoryJournalRepository>()
                .AddSingleton<IPollRepository, InMemoryPollRepository>()
                .AddSingleton<IAggregateVersionStore, InMemoryRegionalAggregateVersionStore>()
                .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
                .AddSingleton<IDatabaseBootstrapHealthChecker, InMemoryBootstrapHealthChecker>(),

            PersistenceMode.ScyllaPostgres => services
                .AddSingleton<IPostgresConnectionFactory>(_ => new PostgresConnectionFactory(options.PostgresConnectionString, options))
                .AddSingleton<IScyllaSessionProvider, ScyllaSessionProvider>()
                .AddSingleton<IAccountRepository, ScyllaAccountRepository>()
                .AddSingleton<INotificationTokenRepository, ScyllaNotificationTokenRepository>()
                .AddSingleton<IEncryptionStateRepository, ScyllaEncryptionStateRepository>()
                .AddSingleton<IAlterRepository, ScyllaAlterRepository>()
                .AddSingleton<IFrontingRepository, ScyllaFrontingRepository>()
                .AddSingleton<IFriendshipRepository, ScyllaFriendshipRepository>()
                .AddSingleton<ITagRepository, ScyllaTagRepository>()
                .AddSingleton<IJournalRepository, ScyllaJournalRepository>()
                .AddSingleton<IPollRepository, ScyllaPollRepository>()
                .AddSingleton<IAggregateVersionStore, ScyllaAggregateVersionStore>()
                .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
                .AddSingleton<IDatabaseBootstrapHealthChecker, ScyllaPostgresBootstrapHealthChecker>(),

            _ => throw new InvalidOperationException($"Unsupported persistence mode: {mode}")
        };
    }
}