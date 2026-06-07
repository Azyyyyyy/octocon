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
    private static bool _added;
    public static void Register()
    {
        if (!_added)
        {
            _added = true;
            ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.ScyllaPostgres, AddPostgresPersistence);
        }
    }

    private static IServiceCollection AddPostgresPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        //Nothing to add in CompatibilityMode
        if (options.CompatibilityMode)
        {
            return services;
        }
        
        return services
            .AddSingleton<IPostgresConnectionFactory, PostgresConnectionFactory>()
            .AddSingleton<ISecretsStore, PostgresSecretsStore>()
            .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, AuthTokenRevocationRepository>()
            .AddHostedService<PostgresMigrationService>();
    }
}