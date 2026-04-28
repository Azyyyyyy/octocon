using Interfold.Domain.Abstractions;
using Interfold.Domain.Auth;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.Postgres;

public class PostgresServiceCollectionExtensions
{
    private static bool _added = false;
    
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
            .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, AuthTokenRevocationRepository>();
    }
}