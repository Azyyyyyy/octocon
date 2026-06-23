using Interfold.Api.Services;
using Interfold.Api.Socket;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Microsoft.AspNetCore.Hosting;
using System.Runtime.CompilerServices;
using Interfold.Contracts.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Interfold.IntegrationTests.TestServices;

public class InterfoldWebApplicationFactory : WebApplicationFactory<Program>
{
    // Live-reloadable override layer. WithConfiguration writes through this provider, which
    // sits at the highest priority of the host's IConfiguration root. Mutating it after the
    // host has built fires its reload token, so anything bound via IOptionsMonitor<T> picks
    // up the new value without a factory rebuild. Settings consumed once at startup (CORS,
    // OAuth scheme registration, IOptions<ClusterConfiguration>, JWT options registration in
    // Program.cs) still need a fresh factory because they snapshot the value during
    // WebApplication.Build().
    private readonly FactoryConfigurationProvider _configProvider = new();
    private static readonly ConditionalWeakTable<HttpClient, InterfoldWebApplicationFactory> ClientFactories = new();
    // Pinned bus instance shared by every IServiceProvider the factory hands out. The default
    // `AddSingleton<IClusterEventBus, InProcessEventBus>()` lifetime relies on the root provider
    // being unique per factory; under TUnit's parallel WebSocket suite we observed multiple
    // service-provider roots resolving distinct InProcessEventBus instances for the same
    // factory, which meant subscriptions registered by the WS pump never saw events published
    // by the command handlers. Constructing the bus eagerly and re-registering it as an instance
    // singleton anchors every resolution path - controllers, WebSocketHandler.RequestServices,
    // hosted background services - on the same bus.
    public InProcessEventBus EventBus { get; } = new();

    public FakeTimeProvider TimeProvider { get; } = new();
    public string DisplayName { get; private set; }
    private readonly string _persistenceType;

    public InterfoldWebApplicationFactory(
        string persistenceType,
        string? displayName = null,
        bool seedInMemorySecretsFromFactoryConfig = true)
    {
        _persistenceType = persistenceType;
        DisplayName = displayName ?? persistenceType;
        _configProvider.Set("OCTOCON_PERSISTENCE", persistenceType);

        // JWT key material and the encryption pepper are not env-bound for the API; SecretsBootstrapService
        // patches them from internal.secrets at startup. For DB-backed runs the SharedDbFixture seeds those
        // secrets into the live store. For in-memory runs there is no real store, so we drive the production
        // env-var seed path (registered in InMemoryServiceCollectionExtensions) by pushing the same PEMs +
        // pepper into the factory's configuration provider — IConfiguration is the single source of truth
        // for `OCTOCON_INMEMORY_SECRETS_SEED__*`, so the in-process tests exercise exactly the same wiring
        // an external runner (e.g. the Kotlin Testcontainers harness) uses against the published image.
        //
        // The keys below use the `:`-form rather than the operator-facing `__`-form because that's
        // the post-normalisation shape: when the real EnvironmentVariablesConfigurationProvider
        // loads an `OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER` env var it rewrites the
        // separator to the config-key delimiter `:`, so SeedFromConfig in InMemoryServiceCollectionExtensions
        // looks up the `:`-form key. FactoryConfigurationProvider.Set stores keys verbatim, so we
        // have to mirror the normalised shape here for the in-process tests to drive the same
        // lookup the production env-var path resolves at runtime.
        //
        // The seedInMemorySecretsFromFactoryConfig opt-out is for InMemorySecretsSeedTests'
        // env-var regression test, which mutates Environment.SetEnvironmentVariable for the `__`
        // form and must NOT be shadowed by the FactoryConfigurationProvider seeds below — that
        // would mask any regression in the real env-var ingestion path (which is exactly the bug
        // the test exists to catch).
        if (seedInMemorySecretsFromFactoryConfig &&
            string.Equals(persistenceType, "inmemory", StringComparison.OrdinalIgnoreCase))
        {
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:ENCRYPTION_PEPPER",           "TEST");
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_ES256_PRIVATE_PEM",  TestDbCredentials.JwtEs256PrivateKeyPem);
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:AUTH_DEEP_LINK_SECRET",       TestDbCredentials.DeepLinkSecret);
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_RSA256_PRIVATE_PEM", TestDbCredentials.JwtRsa256PrivateKeyPem);
        }
    }

    public override string ToString() => DisplayName;

    /// <summary>
    /// Writes <paramref name="value"/> into the factory's highest-priority configuration layer
    /// and reloads the host configuration if the host has already built. Values consumed via
    /// <see cref="IOptionsMonitor{T}"/> propagate immediately; values snapshotted once at
    /// <see cref="WebApplication.Build"/> (CORS, OAuth scheme registration,
    /// <see cref="IOptions{T}"/>) still require constructing a fresh factory.
    /// </summary>
    public InterfoldWebApplicationFactory WithConfiguration(string key, string? value)
    {
        _configProvider.Set(key, value);
        return this;
    }

    public new HttpClient CreateClient()
    {
        return TrackClient(base.CreateClient());
    }

    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        return TrackClient(base.CreateClient(options));
    }

    public new HttpClient CreateDefaultClient(params DelegatingHandler[] handlers)
    {
        return TrackClient(base.CreateDefaultClient(handlers));
    }

    internal static bool TryGetFactory(HttpClient client, out InterfoldWebApplicationFactory factory)
    {
        return ClientFactories.TryGetValue(client, out factory!);
    }

    internal string CreateToken(string systemId)
    {
        var config = Services.GetRequiredService<IConfiguration>();
        var authConfig = config.Get<AuthenticationConfiguration>()
            ?? throw new InvalidOperationException("Authentication configuration was not available for token creation.");

        // ApplyAuthentication leaves the signing material null on purpose (the API consumes
        // it via SecretsBootstrapService at runtime). For the client-side token mint, plug
        // in the same PEM that the fixtures seeded into internal.secrets.
        authConfig.JwtEs256PrivateKeyPem = TestDbCredentials.JwtEs256PrivateKeyPem;

        if (string.IsNullOrWhiteSpace(authConfig.JwtAuthority))
            authConfig.JwtAuthority = "test-authority";

        var jti = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(1);

        return AuthHelper.CreateToken(authConfig, expiresAt, now, jti, systemId);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 1. Push the current snapshot via UseSetting so values are visible to Program.cs at
        //    DI-registration time. AddInterfoldCluster, AddInterfoldAuthChallengeSchemes, JWT
        //    options registration, etc. read IConfiguration immediately when Program.cs runs;
        //    those reads happen before any ConfigureAppConfiguration callback would have a
        //    chance to add the live-reload source. UseSetting flows into builder.Configuration
        //    early enough that Program.cs sees the value.
        foreach (var pair in _configProvider.Snapshot())
        {
            builder.UseSetting(pair.Key, pair.Value);
        }

        // 2. Inject the live-reloadable override provider as the last configuration source so
        //    it also wins over appsettings*.json + env vars at runtime. The provider is a
        //    single instance owned by this factory: subsequent WithConfiguration writes mutate
        //    it in place and fire its reload token, so IOptionsMonitor<T> consumers see
        //    updates without a host rebuild. (Settings consumed once at startup still need
        //    a fresh factory because UseSetting captured the value above.)
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.Add(new FactoryConfigurationSource(_configProvider));
        });

        builder.ConfigureServices(x =>
        {
            x.AddSingleton(this);
            x.Replace(ServiceDescriptor.Singleton<IHttpClientFactory, TestHttpClientFactory>());
            // Replace only the rate limiter with one backed by FakeTimeProvider.
            x.Replace(ServiceDescriptor.Singleton(new SocketJoinRateLimiter(TimeProvider)));
            // See EventBus property comment - register the eagerly created bus as the singleton
            // for both the interface and the concrete type so every service-provider root the
            // factory may construct hands back the SAME instance.
            x.Replace(ServiceDescriptor.Singleton<IClusterEventBus>(EventBus));
            x.Replace(ServiceDescriptor.Singleton(EventBus));

            // DB-backed runs share a single host-wide bootstrap performed by
            // SharedDbFixture.WaitForResourcesAsync (seeding internal.secrets and applying
            // every CQL/SQL migration). Strip the migration hosted services from the per-
            // factory host so the heavy DDL doesn't replay on every WebApplicationFactory
            // build — those rebuilds add tens of seconds of cold start otherwise.
            //
            // SecretsBootstrapService stays registered on purpose: it's the production
            // path that reads internal.secrets and patches IOptionsMonitor<*Configuration>
            // (notably the JWT verification keys IssuingKeyPem / JwtEs256PublicKeyPem and
            // the encryption pepper). Re-running it per factory is cheap (a single
            // SELECT round-trip against the already-seeded msg-db) and exercises the same
            // hosted-service ordering production relies on, so any regression in that
            // service surfaces in tests instead of staging.
            //
            // In-memory persistence does NOT participate in this strip: there are no
            // migration hosted services to remove, and the secrets-store seed flows through
            // the production lookup path (set in this factory's constructor via the
            // `:`-form keys that EnvironmentVariablesConfigurationProvider would expose for
            // `OCTOCON_INMEMORY_SECRETS_SEED__*` env vars) — the same IConfiguration lookup
            // an external container runner triggers via real env vars, so tests exercise the
            // published code path end-to-end.
            if (!string.Equals(_persistenceType, "inmemory", StringComparison.OrdinalIgnoreCase))
            {
                RemoveHostedService<PostgresMigrationService>(x);
                RemoveHostedService<ScyllaMigrationService>(x);
            }
        });
        
        base.ConfigureWebHost(builder);
    }

    /// <summary>
    /// Drops every <see cref="IHostedService"/> registration whose implementation type is
    /// <typeparamref name="TService"/>. Matches both the open-generic
    /// <c>AddHostedService&lt;T&gt;()</c> registration shape (which stores the concrete type
    /// under <see cref="ServiceDescriptor.ImplementationType"/>) and any factory-shape
    /// registration that captures <typeparamref name="TService"/> as its declared
    /// <see cref="ServiceDescriptor.ServiceType"/>.
    /// </summary>
    private static void RemoveHostedService<TService>(IServiceCollection services)
        where TService : class, IHostedService
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(TService))
            {
                services.RemoveAt(i);
            }
        }
    }

    private HttpClient TrackClient(HttpClient client)
    {
        ClientFactories.Remove(client);
        ClientFactories.Add(client, this);
        return client;
    }
    
    public class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly InterfoldWebApplicationFactory _factory;

        public TestHttpClientFactory(InterfoldWebApplicationFactory factory)
        {
            _factory = factory;
        }

        public HttpClient CreateClient(string name)
        {
            return _factory.CreateDefaultClient();
        }
    }

    /// <summary>
    /// Single instance per factory. <see cref="WithConfiguration"/> writes through the inner
    /// provider, which fires its reload token on every Set so <see cref="IOptionsMonitor{T}"/>
    /// re-reads the bound value. Built once at <see cref="ConfigureWebHost"/> time but reused
    /// on every host rebuild — the provider's lifetime matches the factory's, not the host's.
    /// </summary>
    private sealed class FactoryConfigurationSource(FactoryConfigurationProvider provider)
        : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    /// <summary>
    /// Mutable in-memory provider whose <see cref="Set(string, string?)"/> fires
    /// <see cref="ConfigurationProvider.OnReload"/> so <see cref="IConfigurationRoot"/>'s
    /// reload token notifies every <see cref="IOptionsMonitor{T}"/> consumer of the change.
    /// All access is guarded by an internal lock so concurrent <c>WithConfiguration</c>
    /// callers don't race on the underlying dictionary.
    /// </summary>
    private sealed class FactoryConfigurationProvider : ConfigurationProvider
    {
        private readonly Lock _lock = new();

        public override void Set(string key, string? value)
        {
            lock (_lock)
            {
                Data[key] = value;
            }
            OnReload();
        }

        /// <summary>
        /// Stable snapshot of the current key/value pairs for callers that need to forward
        /// them to a different configuration sink (e.g. <see cref="IWebHostBuilder.UseSetting"/>
        /// for startup-snapshot reads in Program.cs). Holds the lock briefly to avoid
        /// enumerator races with concurrent <see cref="Set"/> writes.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string?>> Snapshot()
        {
            lock (_lock)
            {
                return Data.ToArray();
            }
        }
    }
}
