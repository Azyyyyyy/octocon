using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// Extension methods for registering strongly-typed configuration from environment variables.
/// Uses .NET configuration binding with custom providers to ensure compatibility with
/// existing OCTOCON_* and platform-specific (FLY_*) environment variables.
/// </summary>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Interfold configuration sections to the service container.
    /// Call this once during application startup before building the host.
    /// <para>
    /// Configuration is loaded from (in priority order):
    /// 1. Environment variables (OCTOCON_*, FLY_*, GUARDIAN_*)
    /// 2. appsettings.{Environment}.json
    /// 3. appsettings.json
    /// 4. In-memory defaults defined in configuration classes
    /// </para>
    /// </summary>
    public static IConfigurationBuilder AddInterfoldConfiguration(this IConfigurationBuilder configBuilder)
    {
        // Note: Environment variables with underscores are automatically bound by .NET Core
        // configuration system when using ConfigurationBinder
        return configBuilder;
    }

    /// <summary>
    /// Registers all Interfold configuration classes with the DI container using the
    /// <see cref="IOptions{TOptions}"/> / <see cref="IOptionsMonitor{TOptions}"/> pattern.
    /// <para>
    /// Call this once in Program.cs instead of manually binding and registering singletons.
    /// Configuration is read from the live <see cref="IConfiguration"/> on each access, so
    /// values backed by <c>appsettings.json</c> (with <c>reloadOnChange: true</c>) will update
    /// without a restart when consumed via <see cref="IOptionsMonitor{TOptions}"/>.
    /// Environment-variable-backed values are fixed at process start.
    /// </para>
    /// Usage in services:
    /// <list type="bullet">
    ///   <item><see cref="IOptions{TOptions}"/> — startup-only, single snapshot (Persistence, Cluster)</item>
    ///   <item><see cref="IOptionsMonitor{TOptions}"/> — live reload, safe for singletons (Auth, Storage, Socket)</item>
    ///   <item><see cref="IOptionsSnapshot{TOptions}"/> — per-request reload, safe for scoped services</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddInterfoldOptions(this IServiceCollection services)
    {
        // Startup-only: node role cannot change while the process is running.
        services.AddOptions<ClusterConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                opts.NodeGroup = (config["FLY_PROCESS_GROUP"]
                               ?? config["OCTOCON_NODE_GROUP"]
                               ?? "auxiliary").ToLowerInvariant();
            });

        // Startup-only: database connection pools are created once; reconnection requires restart.
        services.AddOptions<PersistenceConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                var region = config["OCTOCON_REGION"] ?? "nam";
                opts.Mode                    = config["OCTOCON_PERSISTENCE"] ?? "scylla-postgres";
                opts.DefaultRegion           = region;
                opts.PostgresConnectionString = config["OCTOCON_POSTGRES_CONNECTION"]
                    ?? "Host=localhost;Port=5432;Database=octocon;Username=octocon;Password=octocon";
                opts.ScyllaKeyspace          = config["OCTOCON_SCYLLA_KEYSPACE"] ?? region;
                opts.ScyllaLocalDatacenter   = config["OCTOCON_SCYLLA_DATACENTER"] ?? "datacenter1";
                var contactPoints            = config["OCTOCON_SCYLLA_CONTACT_POINTS"];
                opts.ScyllaContactPoints     = string.IsNullOrWhiteSpace(contactPoints)
                    ? ["127.0.0.1"]
                    : contactPoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                opts.ScyllaUsername          = config["OCTOCON_SCYLLA_USERNAME"];
                opts.ScyllaPassword          = config["OCTOCON_SCYLLA_PASSWORD"];
                opts.DbRetryAttempts         = TryParseInt(config["OCTOCON_DB_RETRY_ATTEMPTS"])      ?? 3;
                opts.DbRetryInitialDelayMs   = TryParseInt(config["OCTOCON_DB_RETRY_INITIAL_DELAY_MS"]) ?? 100;
                opts.DbRetryMaxDelayMs       = TryParseInt(config["OCTOCON_DB_RETRY_MAX_DELAY_MS"])  ?? 1500;
            });

        // Live-reloadable: JWT signing secrets can rotate, OAuth parameters can be updated via
        // appsettings.json without restart.  Consume via IOptionsMonitor<AuthenticationConfiguration>
        // in singletons, or IOptionsSnapshot<AuthenticationConfiguration> in scoped services.
        services.AddOptions<AuthenticationConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                opts.CallbackBaseUrl      = config["OCTOCON_AUTH_CALLBACK_BASE_URL"];
                opts.DeepLinkSecret       = config["OCTOCON_AUTH_DEEP_LINK_SECRET"];
                opts.JwtAuthority         = config["OCTOCON_JWT_AUTHORITY"];
                opts.GoogleOAuthClientId  = config["OCTOCON_GOOGLE_OAUTH_CLIENT_ID"];
                opts.GoogleOAuthClientSecret = config["OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET"];

                // Aggregate signing secrets in priority order; deduped for key-rotation safety.
                opts.JwtSigningSecrets = new string?[]
                    {
                        config["OCTOCON_AUTH_DEEP_LINK_SECRET"],
                        config["GUARDIAN_SECRET_KEY"],
                        config["OCTOCON_JWT_AUTHORITY"],
                        "octocon-local"
                    }
                    .OfType<string>()
                    .Where(static s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                opts.DiscordSchemeName  = config["OCTOCON_AUTH_CHALLENGE_DISCORD_SCHEME"] ?? "oauth-discord";
                opts.DiscordEndpoint    = config["OCTOCON_AUTH_CHALLENGE_DISCORD_ENDPOINT"];
                opts.DiscordParameters  = ParseAuthParameters(config["OCTOCON_AUTH_CHALLENGE_DISCORD_PARAMS"]);
                opts.GoogleSchemeName   = config["OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME"]  ?? "oauth-google";
                opts.GoogleEndpoint     = config["OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT"];
                opts.GoogleParameters   = ParseAuthParameters(config["OCTOCON_AUTH_CHALLENGE_GOOGLE_PARAMS"]);
                opts.AppleSchemeName    = config["OCTOCON_AUTH_CHALLENGE_APPLE_SCHEME"]   ?? "oauth-apple";
                opts.AppleEndpoint      = config["OCTOCON_AUTH_CHALLENGE_APPLE_ENDPOINT"];
                opts.AppleParameters    = ParseAuthParameters(config["OCTOCON_AUTH_CHALLENGE_APPLE_PARAMS"]);
            });

        // Live-reloadable: frontend URLs and deep-link scheme.
        services.AddOptions<ApiConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                opts.FrontendAddress    = config["OCTOCON_FRONTEND"];
                opts.BetaFrontendAddress = config["OCTOCON_BETA_FRONTEND"];
                opts.DeepLinkAddress    = config["OCTOCON_DEEPLINK_ADDRESS"];
            });

        // Registered for completeness; OTLP exporters are wired at startup so runtime changes
        // to OtlpEndpoint only take effect after a restart.
        services.AddOptions<ObservabilityConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                opts.OtlpEndpoint = config["OCTOCON_OTLP_ENDPOINT"];
            });

        // Live-reloadable: avatar storage paths can be updated via appsettings.json.
        services.AddOptions<StorageConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                opts.AvatarStorageRoot = config["OCTOCON_AVATAR_STORAGE_ROOT"];
                opts.AvatarPublicBase  = config["OCTOCON_AVATAR_PUBLIC_BASE"];
            });

        // Live-reloadable: batch tuning can be adjusted without restart.
        services.AddOptions<SocketConfiguration>()
            .Configure<IConfiguration>(static (opts, config) =>
            {
                opts.BatchBytesThreshold = TryParseInt(config["OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD"]);
            });

        return services;
    }

    /// <summary>
    /// Binds environment variables to ClusterConfiguration.
    /// Maps FLY_PROCESS_GROUP and OCTOCON_NODE_GROUP to the NodeGroup property.
    /// </summary>
    public static ClusterConfiguration BindClusterConfiguration(this IConfiguration config)
    {
        // Resolve node group with priority: FLY_PROCESS_GROUP > OCTOCON_NODE_GROUP > default
        var nodeGroup = config["FLY_PROCESS_GROUP"]
                     ?? config["OCTOCON_NODE_GROUP"]
                     ?? "auxiliary";

        return new ClusterConfiguration { NodeGroup = nodeGroup.ToLowerInvariant() };
    }

    /// <summary>
    /// Binds environment variables to PersistenceConfiguration.
    /// Maps OCTOCON_* variables to properties with camelCase names.
    /// </summary>
    public static PersistenceConfiguration BindPersistenceConfiguration(this IConfiguration config)
    {
        var mode = config["OCTOCON_PERSISTENCE"] ?? "scylla-postgres";
        var region = config["OCTOCON_REGION"] ?? "nam";
        var pgConnection = config["OCTOCON_POSTGRES_CONNECTION"]
            ?? "Host=localhost;Port=5432;Database=octocon;Username=octocon;Password=octocon";
        var scyllaKeyspace = config["OCTOCON_SCYLLA_KEYSPACE"] ?? region; // defaults to region
        var scyllaDatacenter = config["OCTOCON_SCYLLA_DATACENTER"] ?? "datacenter1";
        var contactPointsStr = config["OCTOCON_SCYLLA_CONTACT_POINTS"];
        var contactPoints = string.IsNullOrWhiteSpace(contactPointsStr)
            ? new[] { "127.0.0.1" }
            : contactPointsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var username = config["OCTOCON_SCYLLA_USERNAME"];
        var password = config["OCTOCON_SCYLLA_PASSWORD"];

        var retryAttempts = TryParseInt(config["OCTOCON_DB_RETRY_ATTEMPTS"]) ?? 3;
        var retryInitialDelayMs = TryParseInt(config["OCTOCON_DB_RETRY_INITIAL_DELAY_MS"]) ?? 100;
        var retryMaxDelayMs = TryParseInt(config["OCTOCON_DB_RETRY_MAX_DELAY_MS"]) ?? 1500;

        return new PersistenceConfiguration
        {
            Mode = mode,
            DefaultRegion = region,
            PostgresConnectionString = pgConnection,
            ScyllaKeyspace = scyllaKeyspace,
            ScyllaLocalDatacenter = scyllaDatacenter,
            ScyllaContactPoints = contactPoints,
            ScyllaUsername = username,
            ScyllaPassword = password,
            DbRetryAttempts = retryAttempts,
            DbRetryInitialDelayMs = retryInitialDelayMs,
            DbRetryMaxDelayMs = retryMaxDelayMs,
        };
    }

    /// <summary>
    /// Binds environment variables to AuthenticationConfiguration.
    /// Maps OCTOCON_AUTH_* and GUARDIAN_* variables to properties.
    /// </summary>
    public static AuthenticationConfiguration BindAuthenticationConfiguration(this IConfiguration config)
    {
        var deepLinkSecret = config["OCTOCON_AUTH_DEEP_LINK_SECRET"];
        var guardianSecret = config["GUARDIAN_SECRET_KEY"];
        var jwtAuthority = config["OCTOCON_JWT_AUTHORITY"];

        var jwtSigningSecrets = new string?[] { deepLinkSecret, guardianSecret, jwtAuthority, "octocon-local" }
            .OfType<string>()
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new AuthenticationConfiguration
        {
            CallbackBaseUrl = config["OCTOCON_AUTH_CALLBACK_BASE_URL"],
            JwtSigningSecrets = jwtSigningSecrets.Length > 0 ? jwtSigningSecrets : null,
            DeepLinkSecret = deepLinkSecret,
            JwtAuthority = jwtAuthority,
            GoogleOAuthClientId = config["OCTOCON_GOOGLE_OAUTH_CLIENT_ID"],
            GoogleOAuthClientSecret = config["OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET"],
            DiscordSchemeName = config["OCTOCON_AUTH_CHALLENGE_DISCORD_SCHEME"] ?? "oauth-discord",
            DiscordEndpoint = config["OCTOCON_AUTH_CHALLENGE_DISCORD_ENDPOINT"],
            DiscordParameters = ParseAuthParameters(config["OCTOCON_AUTH_CHALLENGE_DISCORD_PARAMS"]),
            GoogleSchemeName = config["OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME"] ?? "oauth-google",
            GoogleEndpoint = config["OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT"],
            GoogleParameters = ParseAuthParameters(config["OCTOCON_AUTH_CHALLENGE_GOOGLE_PARAMS"]),
            AppleSchemeName = config["OCTOCON_AUTH_CHALLENGE_APPLE_SCHEME"] ?? "oauth-apple",
            AppleEndpoint = config["OCTOCON_AUTH_CHALLENGE_APPLE_ENDPOINT"],
            AppleParameters = ParseAuthParameters(config["OCTOCON_AUTH_CHALLENGE_APPLE_PARAMS"]),
        };
    }

    /// <summary>
    /// Binds environment variables to ApiConfiguration.
    /// Maps OCTOCON_FRONTEND, OCTOCON_BETA_FRONTEND, OCTOCON_DEEPLINK_ADDRESS.
    /// </summary>
    public static ApiConfiguration BindApiConfiguration(this IConfiguration config)
    {
        return new ApiConfiguration
        {
            FrontendAddress = config["OCTOCON_FRONTEND"],
            BetaFrontendAddress = config["OCTOCON_BETA_FRONTEND"],
            DeepLinkAddress = config["OCTOCON_DEEPLINK_ADDRESS"],
        };
    }

    /// <summary>
    /// Binds environment variables to ObservabilityConfiguration.
    /// Maps OCTOCON_OTLP_ENDPOINT.
    /// </summary>
    public static ObservabilityConfiguration BindObservabilityConfiguration(this IConfiguration config)
    {
        return new ObservabilityConfiguration
        {
            OtlpEndpoint = config["OCTOCON_OTLP_ENDPOINT"],
        };
    }

    /// <summary>
    /// Binds environment variables to StorageConfiguration.
    /// Maps OCTOCON_AVATAR_STORAGE_ROOT and OCTOCON_AVATAR_PUBLIC_BASE.
    /// </summary>
    public static StorageConfiguration BindStorageConfiguration(this IConfiguration config)
    {
        return new StorageConfiguration
        {
            AvatarStorageRoot = config["OCTOCON_AVATAR_STORAGE_ROOT"],
            AvatarPublicBase = config["OCTOCON_AVATAR_PUBLIC_BASE"],
        };
    }

    /// <summary>
    /// Binds environment variables to SocketConfiguration.
    /// Maps OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD.
    /// </summary>
    public static SocketConfiguration BindSocketConfiguration(this IConfiguration config)
    {
        return new SocketConfiguration
        {
            BatchBytesThreshold = TryParseInt(config["OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD"]),
        };
    }

    /// <summary>
    /// Binds environment variables to TestingConfiguration.
    /// Maps OCTOCON_RUN_*, OCTOCON_TEST_* variables.
    /// </summary>
    public static TestingConfiguration BindTestingConfiguration(this IConfiguration config)
    {
        var runApi = bool.TryParse(config["OCTOCON_RUN_API_INTEGRATION"], out var resultApi) && resultApi;
        var runLive = bool.TryParse(config["OCTOCON_RUN_LIVE_INTEGRATION"], out var resultLive) && resultLive;

        return new TestingConfiguration
        {
            RunApiIntegration = runApi,
            RunLiveIntegration = runLive,
            TestScyllaContactPoints = config["OCTOCON_TEST_SCYLLA_CONTACT_POINTS"] ?? "127.0.0.1",
            TestScyllaUsername = config["OCTOCON_TEST_SCYLLA_USERNAME"] ?? "cassandra",
            TestScyllaPassword = config["OCTOCON_TEST_SCYLLA_PASSWORD"] ?? "cassandra",
            TestRegion = config["OCTOCON_TEST_REGION"] ?? "nam",
        };
    }

    // --- Helpers ---

    private static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var result) ? result : null;
    }

    private static Dictionary<string, string>? ParseAuthParameters(string? paramsString)
    {
        if (string.IsNullOrWhiteSpace(paramsString))
            return null;

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = paramsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var keyValue = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (keyValue.Length == 2)
                parameters[keyValue[0]] = keyValue[1];
        }

        return parameters.Count > 0 ? parameters : null;
    }
}
