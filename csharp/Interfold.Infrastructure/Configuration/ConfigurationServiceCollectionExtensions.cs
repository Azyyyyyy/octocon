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
            .Configure<IConfiguration>(ApplyCluster);

        // Startup-only: database connection pools are created once; reconnection requires restart.
        services.AddOptions<PersistenceConfiguration>()
            .Configure<IConfiguration>(ApplyPersistence);

        // Live-reloadable: JWT signing secrets can rotate, OAuth parameters can be updated via
        // appsettings.json without restart.  Consume via IOptionsMonitor<AuthenticationConfiguration>
        // in singletons, or IOptionsSnapshot<AuthenticationConfiguration> in scoped services.
        services.AddOptions<AuthenticationConfiguration>()
            .Configure<IConfiguration>(ApplyAuthentication);

        // Live-reloadable: frontend URLs and deep-link scheme.
        services.AddOptions<ApiConfiguration>()
            .Configure<IConfiguration>(ApplyApi);

        // Registered for completeness; OTLP exporters are wired at startup so runtime changes
        // to OtlpEndpoint only take effect after a restart.
        services.AddOptions<ObservabilityConfiguration>()
            .Configure<IConfiguration>(ApplyObservability);

        // Live-reloadable: avatar storage paths can be updated via appsettings.json.
        services.AddOptions<StorageConfiguration>()
            .Configure<IConfiguration>(ApplyStorage);

        // Live-reloadable: batch tuning can be adjusted without restart.
        services.AddOptions<SocketConfiguration>()
            .Configure<IConfiguration>(ApplySocket);

        return services;
    }

    // --- Bind helpers (thin wrappers used by CLI and other non-DI callers) ---

    /// <summary>
    /// Binds environment variables to ClusterConfiguration.
    /// Maps FLY_PROCESS_GROUP and OCTOCON_NODE_GROUP to the NodeGroup property.
    /// </summary>
    public static ClusterConfiguration BindClusterConfiguration(this IConfiguration config)
    {
        var opts = new ClusterConfiguration();
        ApplyCluster(opts, config);
        return opts;
    }

    /// <summary>
    /// Binds environment variables to PersistenceConfiguration.
    /// Maps OCTOCON_* variables to properties with camelCase names.
    /// </summary>
    public static PersistenceConfiguration BindPersistenceConfiguration(this IConfiguration config)
    {
        var opts = new PersistenceConfiguration();
        ApplyPersistence(opts, config);
        return opts;
    }

    /// <summary>
    /// Binds environment variables to AuthenticationConfiguration.
    /// Maps OCTOCON_AUTH_* and GUARDIAN_* variables to properties.
    /// </summary>
    public static AuthenticationConfiguration BindAuthenticationConfiguration(this IConfiguration config)
    {
        var opts = new AuthenticationConfiguration();
        ApplyAuthentication(opts, config);
        return opts;
    }

    /// <summary>
    /// Binds environment variables to TestingConfiguration.
    /// Maps OCTOCON_RUN_*, OCTOCON_TEST_* variables.
    /// </summary>
    public static TestingConfiguration BindTestingConfiguration(this IConfiguration config)
    {
        var runApi  = bool.TryParse(config["OCTOCON_RUN_API_INTEGRATION"],  out var resultApi)  && resultApi;
        var runLive = bool.TryParse(config["OCTOCON_RUN_LIVE_INTEGRATION"], out var resultLive) && resultLive;

        return new TestingConfiguration
        {
            RunApiIntegration       = runApi,
            RunLiveIntegration      = runLive,
            TestScyllaContactPoints = config["OCTOCON_TEST_SCYLLA_CONTACT_POINTS"] ?? "127.0.0.1",
            TestScyllaUsername      = config["OCTOCON_TEST_SCYLLA_USERNAME"] ?? "cassandra",
            TestScyllaPassword      = config["OCTOCON_TEST_SCYLLA_PASSWORD"] ?? "cassandra",
            TestRegion              = config["OCTOCON_TEST_REGION"] ?? "nam",
        };
    }

    // --- Apply methods: single source of truth for each configuration mapping ---

    internal static void ApplyCluster(ClusterConfiguration opts, IConfiguration config)
    {
        opts.NodeGroup = (config["FLY_PROCESS_GROUP"]
                       ?? config["OCTOCON_NODE_GROUP"]
                       ?? "auxiliary").ToLowerInvariant();
    }

    internal static void ApplyPersistence(PersistenceConfiguration opts, IConfiguration config)
    {
        var region           = config["OCTOCON_REGION"] ?? "nam";
        var contactPointsStr = config["OCTOCON_SCYLLA_CONTACT_POINTS"];
        var compatibilityMode = bool.TryParse(config["OCTOCON_COMPATIBILITY_MODE"], out var parsedCompatibilityMode)
            && parsedCompatibilityMode;
        opts.Mode                     = config["OCTOCON_PERSISTENCE"] ?? "scylla-postgres";
        opts.DefaultRegion            = region;
        opts.CompatibilityMode        = compatibilityMode;
        opts.PostgresConnectionString = config["OCTOCON_POSTGRES_CONNECTION"]
            ?? "Host=localhost;Port=5432;Database=octocon;Username=octocon;Password=octocon";
        opts.ScyllaKeyspace           = config["OCTOCON_SCYLLA_KEYSPACE"] ?? region;
        opts.ScyllaLocalDatacenter    = config["OCTOCON_SCYLLA_DATACENTER"] ?? "datacenter1";
        opts.ScyllaContactPoints      = string.IsNullOrWhiteSpace(contactPointsStr)
            ? ["127.0.0.1"]
            : contactPointsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        opts.ScyllaUsername           = config["OCTOCON_SCYLLA_USERNAME"];
        opts.ScyllaPassword           = config["OCTOCON_SCYLLA_PASSWORD"];
        opts.DbRetryAttempts          = TryParseInt(config["OCTOCON_DB_RETRY_ATTEMPTS"])         ?? 3;
        opts.DbRetryInitialDelayMs    = TryParseInt(config["OCTOCON_DB_RETRY_INITIAL_DELAY_MS"]) ?? 100;
        opts.DbRetryMaxDelayMs        = TryParseInt(config["OCTOCON_DB_RETRY_MAX_DELAY_MS"])     ?? 1500;
        opts.HydrationMaxConcurrency = TryParseInt(config["OCTOCON_HYDRATION_MAX_CONCURRENCY"]) ?? 8;
    }

    private static void ApplyAuthentication(AuthenticationConfiguration opts, IConfiguration config)
    {
        opts.CallbackBaseUrl         = config["OCTOCON_AUTH_CALLBACK_BASE_URL"];
        opts.DeepLinkSecret          = config["OCTOCON_AUTH_DEEP_LINK_SECRET"];
        opts.JwtAuthority            = config["OCTOCON_JWT_AUTHORITY"] ?? "octocon-local";
        opts.JwtEs256PrivateKeyPem   = config["OCTOCON_AUTH_EC_PRIVATE_KEY_PEM"];
        opts.JwtEs256PrivateKeyFile  = config["OCTOCON_AUTH_EC_PRIVATE_KEY_FILE"];
        opts.JwtEs256PublicKeyFile   = config["OCTOCON_AUTH_EC_PUBLIC_KEY_FILE"];
        opts.DiscordOAuthClientId    = config["OCTOCON_DISCORD_OAUTH_CLIENT_ID"];
        opts.DiscordOAuthClientSecret = config["OCTOCON_DISCORD_OAUTH_CLIENT_SECRET"];
        opts.GoogleOAuthClientId     = config["OCTOCON_GOOGLE_OAUTH_CLIENT_ID"];
        opts.GoogleOAuthClientSecret = config["OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET"];
        opts.AppleOAuthClientId      = config["OCTOCON_APPLE_OAUTH_CLIENT_ID"];
        opts.AppleOAuthClientSecret  = config["OCTOCON_APPLE_OAUTH_CLIENT_SECRET"];

        var es256VerificationKeys = new List<string>();
        AuthHelper.EnsureEs256KeyMaterial(opts);
        AddIfPresent(config["OCTOCON_AUTH_EC_PUBLIC_KEY_PEM"], es256VerificationKeys);
        AddIfPresent(config["OCTOCON_AUTH_EC_PRIVATE_KEY_PEM"], es256VerificationKeys);
        AddIfPresent(opts.JwtEs256PrivateKeyPem, es256VerificationKeys);

        if (!string.IsNullOrWhiteSpace(opts.JwtEs256PublicKeyFile) && File.Exists(opts.JwtEs256PublicKeyFile))
        {
            AddIfPresent(File.ReadAllText(opts.JwtEs256PublicKeyFile), es256VerificationKeys);
        }

        var publicKeysCsv = config["OCTOCON_AUTH_EC_PUBLIC_KEYS"];
        if (!string.IsNullOrWhiteSpace(publicKeysCsv))
        {
            foreach (var key in publicKeysCsv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddIfPresent(key, es256VerificationKeys);
            }
        }

        opts.JwtEs256VerificationKeyPems = es256VerificationKeys
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

        if (!string.IsNullOrWhiteSpace(opts.DiscordOAuthClientId) && opts.DiscordParameters != null)
        {
            opts.DiscordParameters.Add("client_id", opts.DiscordOAuthClientId);
        }

        if (!string.IsNullOrWhiteSpace(opts.AppleOAuthClientId) && opts.AppleParameters != null)
        {
            opts.AppleParameters["client_id"] = opts.AppleOAuthClientId;
        }
    }

    private static void ApplyApi(ApiConfiguration opts, IConfiguration config)
    {
        opts.FrontendAddress     = config["OCTOCON_FRONTEND"];
        opts.BetaFrontendAddress = config["OCTOCON_BETA_FRONTEND"];
        opts.DeepLinkAddress     = config["OCTOCON_DEEPLINK_ADDRESS"];
    }

    private static void ApplyObservability(ObservabilityConfiguration opts, IConfiguration config)
    {
        opts.OtlpEndpoint = config["OCTOCON_OTLP_ENDPOINT"];
    }

    private static void ApplyStorage(StorageConfiguration opts, IConfiguration config)
    {
        opts.AvatarStorageRoot = config["OCTOCON_AVATAR_STORAGE_ROOT"];
        opts.AvatarPublicBase  = config["OCTOCON_AVATAR_PUBLIC_BASE"];
    }

    private static void ApplySocket(SocketConfiguration opts, IConfiguration config)
    {
        opts.BatchBytesThreshold = TryParseInt(config["OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD"]);
    }

    // --- Helpers ---

    private static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var result) ? result : null;
    }

    private static void AddIfPresent(string? value, ICollection<string> target)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target.Add(value);
        }
    }
    
    internal static Dictionary<string, string>? ParseAuthParameters(string? paramsString)
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
