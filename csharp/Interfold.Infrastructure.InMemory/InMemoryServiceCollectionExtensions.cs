using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory.Repository;
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
            .AddSingleton<ISecretsStore, InMemorySecretsStore>()
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
}