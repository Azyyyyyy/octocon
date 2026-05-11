using Interfold.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using System.Runtime.CompilerServices;
using Interfold.Contracts.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Interfold.IntegrationTests.TestServices;

public class InterfoldWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _configurationOverrides;
    private static readonly ConditionalWeakTable<HttpClient, InterfoldWebApplicationFactory> ClientFactories = new();

    public InterfoldWebApplicationFactory(string persistenceType)
    {
        _configurationOverrides = [];
        _configurationOverrides["OCTOCON_PERSISTENCE"] = persistenceType;
        _configurationOverrides["OCTOCON_AUTH_RSA_PUBLIC_KEY"] = "TEST";
        _configurationOverrides["OCTOCON_AUTH_RSA_PRIVATE_KEY"] = "TEST";
    }

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
        foreach (var pair in _configurationOverrides)
        {
            builder.UseSetting(pair.Key, pair.Value);
        }
        
        builder.ConfigureServices(x =>
        {
            x.AddSingleton(this);
            x.Replace(ServiceDescriptor.Singleton<IHttpClientFactory, TestHttpClientFactory>());
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
