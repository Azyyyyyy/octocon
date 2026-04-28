using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.InMemory;

public static class InMemoryServiceCollectionExtensions
{
    private static bool _added;
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