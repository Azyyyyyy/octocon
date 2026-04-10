using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Auth;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;
using Interfold.Domain.InMemory;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings;
using Interfold.Domain.Settings.Handlers;
using Interfold.Domain.Tags;
using Interfold.Infrastructure.Configuration;
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
        PersistenceConfiguration configuration
    ) => AddInterfoldPersistence(services, mode, cfg =>
    {
        cfg.DefaultRegion = configuration.DefaultRegion;
        cfg.CompatibilityMode = configuration.CompatibilityMode;
        cfg.PostgresConnectionString = configuration.PostgresConnectionString;
        cfg.ScyllaKeyspace           = configuration.ScyllaKeyspace;
        cfg.ScyllaLocalDatacenter    = configuration.ScyllaLocalDatacenter;
        cfg.ScyllaContactPoints      = configuration.ScyllaContactPoints;
        cfg.ScyllaUsername           = configuration.ScyllaUsername;
        cfg.ScyllaPassword           = configuration.ScyllaPassword;
        cfg.DbRetryAttempts          = configuration.DbRetryAttempts;
        cfg.DbRetryInitialDelayMs    = configuration.DbRetryInitialDelayMs;
        cfg.DbRetryMaxDelayMs        = configuration.DbRetryMaxDelayMs;
    });

    /// <summary>
    /// Reads persistence settings from environment variables and registers the appropriate
    /// persistence services. The mode is derived from OCTOCON_PERSISTENCE.
    /// </summary>
    public static IServiceCollection AddInterfoldPersistence(
        this IServiceCollection services,
        IConfiguration config)
    {
        var opts = new PersistenceConfiguration();
        ConfigurationServiceCollectionExtensions.ApplyPersistence(opts, config);
        var mode = opts.Mode switch
        {
            "inmemory"        => PersistenceMode.InMemory,
            "scylla-postgres" => PersistenceMode.ScyllaPostgres,
            var x             => throw new InvalidOperationException($"Unsupported persistence mode: {x}")
        };
        return services.AddInterfoldPersistence(mode, opts);
    }

    public static IServiceCollection AddInterfoldPersistence(
        this IServiceCollection services,
        PersistenceMode mode,
        Action<PersistenceConfiguration>? configure = null
    )
    {
        var options = new PersistenceConfiguration();
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
                .AddSingleton<IPollRepository, InMemoryPollRepository>()
                .AddSingleton<IAlterRepository>(sp => new InMemoryRegionalAlterRepository(
                    sp.GetRequiredService<IRegionContext>(),
                    sp.GetRequiredService<IFriendshipRepository>(),
                    sp.GetRequiredService<ISettingsFieldRepository>(),
                    sp.GetRequiredService<IPollRepository>()))
                .AddSingleton<IFrontingRepository, InMemoryRegionalFrontingRepository>()
                .AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>()
                .AddSingleton<ITagRepository, InMemoryTagRepository>()
                .AddSingleton<IJournalRepository, InMemoryJournalRepository>()
                .AddSingleton<IAggregateVersionStore, InMemoryRegionalAggregateVersionStore>()
                .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
                .AddSingleton<IAuthTokenRevocationRepository, InMemoryAuthTokenRevocationRepository>()
                .AddSingleton<IDatabaseBootstrapHealthChecker>(sp => 
                    new InMemoryBootstrapHealthChecker(sp.GetRequiredService<IAlterRepository>()))
                .AddSingleton<IOperationalHealthChecker>(sp =>
                    sp.GetRequiredService<IDatabaseBootstrapHealthChecker>() as IOperationalHealthChecker ?? throw new InvalidOperationException()),

            PersistenceMode.ScyllaPostgres => AddScyllaPostgresPersistence(services, options),

            _ => throw new InvalidOperationException($"Unsupported persistence mode: {mode}")
        };
    }

    private static IServiceCollection AddScyllaPostgresPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        var pipeline = services
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
                .AddSingleton<IAggregateVersionStore, ScyllaAggregateVersionStore>();

        if (!options.CompatibilityMode)
        {
            pipeline = pipeline
                .AddSingleton<IPostgresConnectionFactory>(_ => new PostgresConnectionFactory(options.PostgresConnectionString, options))
                .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
                .AddSingleton<IAuthTokenRevocationRepository, AuthTokenRevocationRepository>()
                .AddSingleton<IDatabaseBootstrapHealthChecker>(sp =>
                    new ScyllaPostgresBootstrapHealthChecker(
                        sp.GetRequiredService<IPostgresConnectionFactory>(),
                        sp.GetRequiredService<IScyllaSessionProvider>(),
                        sp.GetRequiredService<IAlterRepository>(),
                        options));
        }
        else
        {
            pipeline = pipeline
                .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
                .AddSingleton<IAuthTokenRevocationRepository, InMemoryAuthTokenRevocationRepository>()
                .AddSingleton<IDatabaseBootstrapHealthChecker>(sp =>
                    new ScyllaPostgresBootstrapHealthChecker(
                        null,
                        sp.GetRequiredService<IScyllaSessionProvider>(),
                        sp.GetRequiredService<IAlterRepository>(),
                        options));
        }

        return pipeline
            .AddSingleton<IOperationalHealthChecker>(sp =>
                sp.GetRequiredService<IDatabaseBootstrapHealthChecker>() as IOperationalHealthChecker ?? throw new InvalidOperationException());
    }

    public static IServiceCollection AddInterfoldDomainHandlers(this IServiceCollection services) =>
        services
            .AddSingleton<CreateAlterCommandHandler>()
            .AddSingleton<UpdateAlterCommandHandler>()
            .AddSingleton<DeleteAlterCommandHandler>()
            .AddSingleton<StartFrontCommandHandler>()
            .AddSingleton<EndFrontCommandHandler>()
            .AddSingleton<BulkUpdateFrontCommandHandler>()
            .AddSingleton<SetFrontCommandHandler>()
            .AddSingleton<SetPrimaryFrontCommandHandler>()
            .AddSingleton<DeleteFrontByIdCommandHandler>()
            .AddSingleton<UpdateFrontCommentCommandHandler>()
            .AddSingleton<UpdateUsernameCommandHandler>()
            .AddSingleton<UpdateDescriptionCommandHandler>()
            .AddSingleton<AddPushTokenCommandHandler>()
            .AddSingleton<RemovePushTokenCommandHandler>()
            .AddSingleton<SetupEncryptionCommandHandler>()
            .AddSingleton<RecoverEncryptionCommandHandler>()
            .AddSingleton<ResetEncryptionCommandHandler>()
            .AddSingleton<UploadAvatarCommandHandler>()
            .AddSingleton<DeleteAvatarCommandHandler>()
            .AddSingleton<ImportPkCommandHandler>()
            .AddSingleton<ImportSpCommandHandler>()
            .AddSingleton<UnlinkDiscordCommandHandler>()
            .AddSingleton<UnlinkEmailCommandHandler>()
            .AddSingleton<UnlinkAppleCommandHandler>()
            .AddSingleton<DeleteAccountCommandHandler>()
            .AddSingleton<WipeAltersCommandHandler>()
            .AddSingleton<CreateFieldCommandHandler>()
            .AddSingleton<UpdateFieldCommandHandler>()
            .AddSingleton<DeleteFieldCommandHandler>()
            .AddSingleton<RelocateFieldCommandHandler>()
            .AddSingleton<CreateTagCommandHandler>()
            .AddSingleton<UpdateTagCommandHandler>()
            .AddSingleton<DeleteTagCommandHandler>()
            .AddSingleton<AttachAlterToTagCommandHandler>()
            .AddSingleton<DetachAlterFromTagCommandHandler>()
            .AddSingleton<SetParentTagCommandHandler>()
            .AddSingleton<RemoveParentTagCommandHandler>()
            .AddSingleton<CreatePollCommandHandler>()
            .AddSingleton<UpdatePollCommandHandler>()
            .AddSingleton<DeletePollCommandHandler>()
            .AddSingleton<CreateGlobalJournalEntryCommandHandler>()
            .AddSingleton<UpdateGlobalJournalEntryCommandHandler>()
            .AddSingleton<DeleteGlobalJournalEntryCommandHandler>()
            .AddSingleton<SetGlobalJournalLockedCommandHandler>()
            .AddSingleton<SetGlobalJournalPinnedCommandHandler>()
            .AddSingleton<AttachAlterToGlobalJournalCommandHandler>()
            .AddSingleton<DetachAlterFromGlobalJournalCommandHandler>()
            .AddSingleton<CreateAlterJournalEntryCommandHandler>()
            .AddSingleton<UpdateAlterJournalEntryCommandHandler>()
            .AddSingleton<DeleteAlterJournalEntryCommandHandler>()
            .AddSingleton<SetAlterJournalLockedCommandHandler>()
            .AddSingleton<SetAlterJournalPinnedCommandHandler>()
            .AddSingleton<RemoveFriendshipCommandHandler>()
            .AddSingleton<SetFriendTrustCommandHandler>()
            .AddSingleton<SendFriendRequestCommandHandler>()
            .AddSingleton<AcceptFriendRequestCommandHandler>()
            .AddSingleton<RejectFriendRequestCommandHandler>()
            .AddSingleton<CancelFriendRequestCommandHandler>();
}

