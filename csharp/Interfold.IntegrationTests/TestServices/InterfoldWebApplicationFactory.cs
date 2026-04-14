using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
        base.ConfigureWebHost(builder);
    }
}
