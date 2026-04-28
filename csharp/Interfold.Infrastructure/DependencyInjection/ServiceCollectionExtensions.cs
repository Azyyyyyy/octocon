using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings.Handlers;
using Interfold.Domain.Tags;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    private static readonly ConcurrentDictionary<PersistenceMode, List<Func<IServiceCollection, PersistenceConfiguration, IServiceCollection>>> PersistenceReg = [];

    internal static void AddPersistenceMode(PersistenceMode persistenceMode, Func<IServiceCollection, PersistenceConfiguration, IServiceCollection> reg)
    {
        if (!PersistenceReg.TryGetValue(persistenceMode, out var regs))
        {
            regs = [];
            PersistenceReg.GetOrAdd(persistenceMode, regs);
        }

        regs.Add(reg);
    }
    
    public static IServiceCollection AddInterfoldPersistence(
        this IServiceCollection services,
        PersistenceMode mode,
        PersistenceConfiguration configuration
    ) => AddInterfoldPersistence(services, mode, cfg =>
    {
        cfg.DefaultRegion            = configuration.DefaultRegion;
        cfg.CompatibilityMode        = configuration.CompatibilityMode;
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

        if (!Enum.IsDefined(mode))
        {
            throw new InvalidOperationException($"Unsupported persistence mode: {mode}");
        }

        if (PersistenceReg.TryGetValue(mode, out var list))
        {
            foreach (Func<IServiceCollection, PersistenceConfiguration, IServiceCollection> func in list)
            {
                func(services, options);
            }

            return services;
        }
        
        throw new InvalidOperationException($"Persistence mode has not yet been implemented: {mode}");
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

