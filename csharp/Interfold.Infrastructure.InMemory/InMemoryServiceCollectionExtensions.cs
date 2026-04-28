using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Auth;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings;
using Interfold.Domain.Tags;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.InMemory;

public class InMemoryServiceCollectionExtensions
{
    private static bool _added = false;
    
    public static void Register()
    {
        if (!_added)
        {
            _added = true;
            ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.InMemory, AddInMemoryPersistence);
        }
    }

    private static IServiceCollection AddInMemoryPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        var pipeline = services
            .AddSingleton<IRegionContext>(_ => new InMemoryRegionContext(options.DefaultRegion))
            .AddSingleton<IAccountRepository, InMemoryRegionalAccountRepository>()
            .AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>()
            .AddSingleton<IEncryptionStateRepository, InMemoryEncryptionStateRepository>()
            .AddSingleton<ISettingsFieldRepository, InMemorySettingsFieldRepository>()
            .AddSingleton<IPollRepository, InMemoryPollRepository>()
            .AddSingleton<IAlterRepository, InMemoryRegionalAlterRepository>()
            .AddSingleton<IFrontingRepository, InMemoryRegionalFrontingRepository>()
            .AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>()
            .AddSingleton<ITagRepository, InMemoryTagRepository>()
            .AddSingleton<IJournalRepository, InMemoryJournalRepository>()
            .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, InMemoryAuthTokenRevocationRepository>();
        
        if (!options.CompatibilityMode)
        {
            return pipeline;
        }

        return pipeline
            .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, InMemoryAuthTokenRevocationRepository>();
    }
}