using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings;
using Interfold.Domain.Tags;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Persistence;
using Interfold.Infrastructure.Scylla.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.Scylla;

public static class ScyllaServiceCollectionExtensions
{
    private static bool _added = false;
    
    public static void Register()
    {
        if (!_added)
        {
            _added = true;
            ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.ScyllaPostgres, AddScyllaPersistence);
        }
    }
    
    private static IServiceCollection AddScyllaPersistence(
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
                .AddSingleton<IPollRepository, ScyllaPollRepository>();

        return pipeline;
    }
}