using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.DependencyInjection;

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

        // Startup-baked: AuthenticationConfiguration is a hybrid of env-bound values (OAuth
        // client IDs, callback base URL, JWT authority) and secret-store-bound values
        // (RSA/ES256 signing material, deep-link secret, encryption pepper, OAuth client
        // secrets). SecretsBootstrapService patches the secret fields directly into the
        // IOptionsMonitor.CurrentValue snapshot at startup. Wiring an
        // IOptionsChangeTokenSource here would cause every IConfiguration reload to
        // re-run ApplyAuthentication and overwrite the patched secrets with the empty
        // initial values, breaking JWT verification and the encryption pepper guard.
        // Treat auth as startup-only until the secret bootstrap moves to
        // IPostConfigureOptions or a dedicated reload-aware patcher.
        services.AddOptions<AuthenticationConfiguration>()
            .Configure<IConfiguration>(ApplyAuthentication);

        // Registered for completeness; OTLP exporters are wired at startup so runtime changes
        // to OtlpEndpoint only take effect after a restart.
        services.AddOptions<ObservabilityConfiguration>()
            .Configure<IConfiguration>(ApplyObservability);

        // Live-reloadable: avatar storage paths can be updated via appsettings.json.
        AddLiveReloadable<StorageConfiguration>(services, ApplyStorage);

        // Live-reloadable: batch tuning can be adjusted without restart.
        AddLiveReloadable<SocketConfiguration>(services, ApplySocket);

        return services;
    }

    /// <summary>
    /// Registers a live-reloadable strongly-typed options instance. Calling
    /// <c>AddOptions&lt;T&gt;().Configure&lt;IConfiguration&gt;(...)</c> on its own only registers an
    /// <see cref="IConfigureOptions{TOptions}"/> — that wires the apply callback into the snapshot
    /// pipeline, but it does NOT subscribe <see cref="IOptionsMonitor{TOptions}"/> to configuration
    /// reload events. Without an <see cref="IOptionsChangeTokenSource{TOptions}"/> the first access
    /// to <c>CurrentValue</c> caches whatever the apply callback produced and ignores subsequent
    /// <see cref="IConfigurationRoot.Reload"/> calls. <see cref="ConfigurationChangeTokenSource{TOptions}"/>
    /// bridges the configuration's reload token into the options pipeline so consumers actually see
    /// updates, which is what the integration tests' <c>WithConfiguration</c> live-reload contract
    /// depends on.
    /// </summary>
    private static OptionsBuilder<TOptions> AddLiveReloadable<TOptions>(
        IServiceCollection services,
        Action<TOptions, IConfiguration> apply)
        where TOptions : class
    {
        var builder = services.AddOptions<TOptions>().Configure<IConfiguration>(apply);
        services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(sp =>
            new ConfigurationChangeTokenSource<TOptions>(
                Options.DefaultName, sp.GetRequiredService<IConfiguration>()));
        return builder;
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

    // --- Apply methods: single source of truth for each configuration mapping ---

    internal static void ApplyCluster(ClusterConfiguration opts, IConfiguration config)
    {
        opts.NodeGroup = (config["FLY_PROCESS_GROUP"]
                       ?? config["OCTOCON_NODE_GROUP"]
                       ?? "auxiliary").ToLowerInvariant();
    }

    internal static void ApplyPersistence(PersistenceConfiguration opts, IConfiguration config)
    {
        var keyspace = config["OCTOCON_SCYLLA_KEYSPACE"] ?? "nam";
        opts.Mode = config["OCTOCON_PERSISTENCE"] ?? "scylla-postgres";
        opts.ScyllaKeyspace = keyspace;
        opts.PostgresConnectionString = config["OCTOCON_POSTGRES_CONNECTION"]
            ?? "Host=localhost;Port=5432;Database=interfold;Username=interfold;Password=interfold";
        opts.IsSingleScyllaInstance = bool.TryParse(config["OCTOCON_SINGLE_SCYLLA_INSTANCE"], out var singleKs) && singleKs;
        opts.DbRetryAttempts = TryParseInt(config["OCTOCON_DB_RETRY_ATTEMPTS"]) ?? 3;
        opts.DbRetryInitialDelayMs = TryParseInt(config["OCTOCON_DB_RETRY_INITIAL_DELAY_MS"]) ?? 100;
        opts.DbRetryMaxDelayMs = TryParseInt(config["OCTOCON_DB_RETRY_MAX_DELAY_MS"]) ?? 1500;
        opts.HydrationMaxConcurrency = TryParseInt(config["OCTOCON_HYDRATION_MAX_CONCURRENCY"]) ?? 8;
    }

    /// <summary>
    /// Initial bind of <see cref="AuthenticationConfiguration"/> from env. JWT signing keys
    /// (RSA + ES256), the deep-link HMAC secret, and the encryption pepper override get
    /// patched in over the top by <c>SecretsBootstrapService</c> on startup from
    /// <c>internal.secrets</c>; the rest of the fields stay env-bound. OAuth client IDs are
    /// public values and remain env-only; the matching client secrets live in the store and
    /// are also overridden by <c>SecretsBootstrapService</c>.
    /// </summary>
    private static void ApplyAuthentication(AuthenticationConfiguration opts, IConfiguration config)
    {
        opts.CallbackBaseUrl = config["OCTOCON_AUTH_CALLBACK_BASE_URL"];
        opts.JwtAuthority = config["OCTOCON_JWT_AUTHORITY"] ?? "octocon-local";

        // OAuth client IDs are public values (they appear in OAuth redirect URLs); keep them
        // env-bound. The matching secrets are placeholders here and get overwritten by
        // SecretsBootstrapService from the store before they're consumed.
        opts.DiscordOAuthClientId = config["OCTOCON_DISCORD_OAUTH_CLIENT_ID"];
        opts.DiscordOAuthClientSecret = config["OCTOCON_DISCORD_OAUTH_CLIENT_SECRET"];
        opts.GoogleOAuthClientId = config["OCTOCON_GOOGLE_OAUTH_CLIENT_ID"];
        opts.GoogleOAuthClientSecret = config["OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET"];
        opts.AppleOAuthClientId = config["OCTOCON_APPLE_OAUTH_CLIENT_ID"];
        opts.AppleOAuthClientSecret = config["OCTOCON_APPLE_OAUTH_CLIENT_SECRET"];

        // JWT signing material, deep-link secret, and the encryption pepper are intentionally
        // left null/empty here. SecretsBootstrapService.StartingAsync runs before any consumer
        // touches these fields (its registration order in Program.cs sits ahead of every
        // migration service and request-time handler) and fills them from
        // `auth:jwt_rsa256_private_pem`, `auth:jwt_es256_private_pem`, `auth:deep_link_secret`,
        // and `encryption:pepper` respectively. The pepper row is enforced as required
        // inside SecretsBootstrapService — if it's missing the API refuses to boot. The
        // JWT and deep-link rows fail at first signing/verification (visible in startup
        // logs) rather than at boot, matching the pattern established for those fields.
        opts.Rsa256PublicKey = string.Empty;
        opts.Rsa256PrivateKey = string.Empty;
        opts.JwtEs256PrivateKeyPem = null;
        opts.JwtEs256VerificationKeyPems = null;
        opts.DeepLinkSecret = null;
        opts.EncryptionPepper = null!;

        // The OAuth challenge query parameters (scopes / response_type / response_mode) plus
        // each provider's scheme name + authorization endpoint are constants in
        // OAuthChallengeServiceCollectionExtensions — the scopes are functionally tied to
        // the data the callback handlers read, so changing them requires a code change. The
        // only per-deployment value (client_id) is injected directly during scheme
        // registration from the OAuthClientId fields above.
    }

    private static void ApplyObservability(ObservabilityConfiguration opts, IConfiguration config)
    {
        opts.OtlpEndpoint = config["OCTOCON_OTLP_ENDPOINT"];
    }

    private static void ApplyStorage(StorageConfiguration opts, IConfiguration config)
    {
        opts.AvatarStorageRoot = config["OCTOCON_AVATAR_STORAGE_ROOT"];
        opts.AvatarPublicBase = config["OCTOCON_AVATAR_PUBLIC_BASE"];
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
}
