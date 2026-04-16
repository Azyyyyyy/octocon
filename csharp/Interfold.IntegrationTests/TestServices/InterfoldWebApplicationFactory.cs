using Interfold.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Interfold.IntegrationTests.TestServices;

public class InterfoldWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _configurationOverrides = new();

    public InterfoldWebApplicationFactory WithConfiguration(string key, string? value)
    {
        _configurationOverrides[key] = value;
        return this;
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
