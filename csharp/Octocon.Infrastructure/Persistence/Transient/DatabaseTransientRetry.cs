using Cassandra;
using Npgsql;

namespace Octocon.Infrastructure.Persistence.Transient;

internal static class DatabaseTransientRetry
{
    private const int MaxBackoffExponent = 8;

    public static Task ExecuteScyllaAsync(
        Func<Task> operation,
        PersistenceRegistrationOptions options,
        CancellationToken cancellationToken = default
    ) => ExecuteAsync(operation, options, IsScyllaTransient, cancellationToken);

    public static Task<T> ExecuteScyllaAsync<T>(
        Func<Task<T>> operation,
        PersistenceRegistrationOptions options,
        CancellationToken cancellationToken = default
    ) => ExecuteAsync(operation, options, IsScyllaTransient, cancellationToken);

    public static Task ExecutePostgresAsync(
        Func<Task> operation,
        PersistenceRegistrationOptions options,
        CancellationToken cancellationToken = default
    ) => ExecuteAsync(operation, options, IsPostgresTransient, cancellationToken);

    public static Task<T> ExecutePostgresAsync<T>(
        Func<Task<T>> operation,
        PersistenceRegistrationOptions options,
        CancellationToken cancellationToken = default
    ) => ExecuteAsync(operation, options, IsPostgresTransient, cancellationToken);

    private static async Task ExecuteAsync(
        Func<Task> operation,
        PersistenceRegistrationOptions options,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken
    )
    {
        await ExecuteAsync(async () =>
        {
            await operation();
            return true;
        }, options, isTransient, cancellationToken);
    }

    private static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        PersistenceRegistrationOptions options,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken
    )
    {
        var attempts = Math.Max(1, options.DbRetryAttempts);
        var initialDelayMs = Math.Max(1, options.DbRetryInitialDelayMs);
        var maxDelayMs = Math.Max(initialDelayMs, options.DbRetryMaxDelayMs);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < attempts && isTransient(ex))
            {
                var delay = ComputeBackoff(attempt, initialDelayMs, maxDelayMs);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static TimeSpan ComputeBackoff(int attempt, int initialDelayMs, int maxDelayMs)
    {
        var exponent = Math.Min(attempt - 1, MaxBackoffExponent);
        var baseDelay = initialDelayMs * (1 << exponent);
        var capped = Math.Min(baseDelay, maxDelayMs);
        var jittered = Random.Shared.Next(capped / 2, capped + 1);
        return TimeSpan.FromMilliseconds(jittered);
    }

    private static bool IsScyllaTransient(Exception exception) => exception switch
    {
        NoHostAvailableException => true,
        OperationTimedOutException => true,
        ReadTimeoutException => true,
        WriteTimeoutException => true,
        TimeoutException => true,
        _ => false
    };

    private static bool IsPostgresTransient(Exception exception) => exception switch
    {
        NpgsqlException npgsqlException => npgsqlException.IsTransient,
        TimeoutException => true,
        _ => false
    };
}
