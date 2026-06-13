using Interfold.Api.Socket;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Coordination;
using Microsoft.AspNetCore.Hosting;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Interfold.Contracts.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Interfold.IntegrationTests.TestServices;

public class InterfoldWebApplicationFactory : WebApplicationFactory<Program>
{
    // ConcurrentDictionary (rather than Dictionary) because tests share a factory when
    // [ClassDataSource(Shared = SharedType.PerTestSession)] is used: when those tests
    // run in parallel (which is the default after the WebSocketTests [NotInParallel]
    // removal) multiple WithConfiguration callers can write at once. Note: writes that
    // arrive AFTER the first CreateClient/Server have no effect on the running host —
    // ConfigureWebHost is only invoked during the initial build. Use the constructor or
    // the fixture's InitializeAsync to seed values that the host must actually observe.
    private readonly ConcurrentDictionary<string, string?> _configurationOverrides;
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

    public InterfoldWebApplicationFactory(string persistenceType, string? displayName = null)
    {
        DisplayName = displayName ?? persistenceType;
        _configurationOverrides = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        _configurationOverrides["OCTOCON_PERSISTENCE"] = persistenceType;
        _configurationOverrides["OCTOCON_AUTH_RSA_PUBLIC_KEY"] = "TEST";
        _configurationOverrides["OCTOCON_AUTH_RSA_PRIVATE_KEY"] = "TEST";
        _configurationOverrides["OCTOCON_ENCRYPTION_PEPPER"] = "TEST";
    }

    public override string ToString() => DisplayName;

    public InterfoldWebApplicationFactory WithConfiguration(string key, string? value)
    {
        _configurationOverrides[key] = value;
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
        var authConfig = config.Get<AuthenticationConfiguration>();

        if (authConfig is null)
            throw new InvalidOperationException("Authentication configuration was not available for token creation.");
        AuthHelper.EnsureEs256KeyMaterial(authConfig);

        if (string.IsNullOrWhiteSpace(authConfig.JwtAuthority))
            authConfig.JwtAuthority = "test-authority";

        var jti = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(1);

        return AuthHelper.CreateToken(authConfig, expiresAt, now, jti, systemId);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Snapshot the override pairs to avoid InvalidOperationException if another
        // thread mutates the dictionary mid-enumeration. ConcurrentDictionary's
        // enumerator is already safe to iterate while writes occur, but materialising
        // explicitly keeps the call site readable and intent clear.
        foreach (var pair in _configurationOverrides.ToArray())
        {
            builder.UseSetting(pair.Key, pair.Value);
        }
        
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
        });
        
        base.ConfigureWebHost(builder);
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
}
