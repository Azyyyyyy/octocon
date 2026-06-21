using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.Postgres;

public static class PostgresServiceCollectionExtensions
{
    // See InMemoryServiceCollectionExtensions.Registration for the full rationale: the
    // previous Interlocked.Exchange gate let a second caller observe the "already
    // registered" flag before the first caller had populated PersistenceReg, which surfaced
    // as a "Persistence mode has not yet been implemented" throw under parallel
    // WebApplicationFactory<Program> host builds. Lazy<bool> (ExecutionAndPublication mode)
    // serialises observers behind the registration call so every Register() return
    // guarantees PersistenceReg[ScyllaPostgres] contains this adapter.
    private static readonly Lazy<bool> Registration = new(() =>
    {
        ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.ScyllaPostgres, AddPostgresPersistence);
        return true;
    });

    public static void Register() => _ = Registration.Value;

    private static IServiceCollection AddPostgresPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        return services
            .AddSingleton<IPostgresConnectionFactory, PostgresConnectionFactory>()
            .AddSingleton<ISecretsStore, PostgresSecretsStore>()
            .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, AuthTokenRevocationRepository>()
            .AddHostedService<PostgresMigrationService>();
    }
}