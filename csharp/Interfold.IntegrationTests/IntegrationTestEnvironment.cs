using Microsoft.Extensions.Configuration;

namespace Interfold.IntegrationTests;

/// <summary>
/// Centralized helper for accessing environment variables in integration tests.
/// Supports reading configuration from environment variables with fallback values.
/// </summary>
public static class IntegrationTestEnvironment
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    /// <summary>
    /// Gets an environment variable value or returns a fallback if not set/empty.
    /// </summary>
    public static string GetVariable(string key, string fallback = "")
    {
        var value = Configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// Checks if a boolean environment variable is enabled.
    /// </summary>
    public static bool IsEnabled(string key)
    {
        var value = Configuration[key];
        return bool.TryParse(value, out var enabled) && enabled;
    }

    /// <summary>
    /// Checks if the API integration tests should run (OCTOCON_RUN_API_INTEGRATION=true).
    /// </summary>
    public static bool ShouldRunApiIntegration => IsEnabled("OCTOCON_RUN_API_INTEGRATION");

    /// <summary>
    /// Checks if live integration tests should run (OCTOCON_RUN_LIVE_INTEGRATION=true).
    /// </summary>
    public static bool ShouldRunLiveIntegration => IsEnabled("OCTOCON_RUN_LIVE_INTEGRATION");

    /// <summary>
    /// Gets the PostgreSQL connection string if available.
    /// </summary>
    public static bool HasPostgresConnection => !string.IsNullOrWhiteSpace(Configuration["OCTOCON_POSTGRES_CONNECTION"]);

    /// <summary>
    /// Gets the PostgreSQL connection string.
    /// </summary>
    public static string? PostgresConnection => Configuration["OCTOCON_POSTGRES_CONNECTION"];
}
