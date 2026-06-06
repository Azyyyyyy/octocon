using System.Reflection;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Interfold.Infrastructure.Postgres;

/// <summary>
/// Applies embedded SQL migrations at startup using the admin connection string.
/// Runs before the app accepts traffic (IHostedLifecycleService.StartingAsync).
/// </summary>
public sealed class PostgresMigrationService(
    PersistenceConfiguration options,
    ILogger<PostgresMigrationService> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PostgresAdminConnectionString))
        {
            logger.LogInformation("[pg-migrate] No admin connection string configured — skipping migrations.");
            return;
        }

        logger.LogInformation("[pg-migrate] Applying PostgreSQL schema migrations...");

        await using var connection = new NpgsqlConnection(options.PostgresAdminConnectionString);
        await connection.OpenAsync(cancellationToken);

        var migrations = GetMigrationScripts();
        foreach (var (name, sql) in migrations)
        {
            logger.LogInformation("[pg-migrate] Applying: {Migration}", name);
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("[pg-migrate] All migrations applied ({Count} files).", migrations.Count);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static List<(string Name, string Sql)> GetMigrationScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "Interfold.Infrastructure.Postgres.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Name: n[prefix.Length..], Sql: reader.ReadToEnd());
            })
            .ToList();
    }
}
