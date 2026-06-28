using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigPhase.ResolveDerivedDefaults"/>. The helper is the
/// single source of truth for "what does an empty <see cref="ApiRuntimeSection"/> field
/// resolve to?" — both the interactive form's Show callbacks and the non-interactive
/// <see cref="ConfigPhase.Validate"/> path call it, so its behaviour gets pinned here in
/// isolation.
///
/// Three rules under test:
/// <list type="number">
///   <item>The scheme follows <see cref="DeploymentSection.WebHttps"/>: false → http, true → https.</item>
///   <item>The primary host (first non-CIDR entry of <see cref="DeploymentSection.Hosts"/>) wins
///         for the two single-value fields (<see cref="ApiRuntimeSection.CallbackBaseUrl"/> /
///         <see cref="ApiRuntimeSection.JwtAuthority"/>). CORS gets one entry per non-CIDR host.
///         IPv6 literals are bracket-wrapped per RFC 3986 §3.2.2.</item>
///   <item>A non-empty stored value always wins over the derived default — derivation only
///         fills blanks, it never overwrites.</item>
/// </list>
/// </summary>
public sealed class ConfigPhaseResolveDerivedDefaultsTests
{
    /// <summary>
    /// Builds a fresh <see cref="BootstrapConfig"/> with explicit deployment values so the
    /// derivation inputs are unambiguous, and an empty <see cref="ApiRuntimeSection"/> so
    /// every test starts from the "needs derivation" state.
    /// </summary>
    private static BootstrapConfig MakeConfigWithEmptyApiRuntime(
        bool webHttps = false,
        params string[] hosts)
    {
        var cfg = new BootstrapConfig
        {
            Deployment =
            {
                Hosts = hosts.Length > 0 ? [.. hosts] : ["api.example.com"],
                WebHttps = webHttps,
            },
        };
        cfg.ApiRuntime.CallbackBaseUrl = string.Empty;
        cfg.ApiRuntime.JwtAuthority = string.Empty;
        cfg.ApiRuntime.CorsAllowedOrigins = [];
        return cfg;
    }

    [Test]
    public async Task DerivesHttpSchemeWhenWebHttpsIsFalse()
    {
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: "api.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("http://api.example.com");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("http://api.example.com");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("http://api.example.com");
    }

    [Test]
    public async Task DerivesHttpsSchemeWhenWebHttpsIsTrue()
    {
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://api.example.com");
    }

    [Test]
    public async Task PrimaryHostWinsForSingleValueFields()
    {
        // Two hosts supplied. CallbackBaseUrl and JwtAuthority must take only the FIRST
        // non-CIDR one — they're single-value scalars, and the bootstrapper's contract says
        // the operator's primary public host is what callbacks and JWT iss claims target.
        var cfg = MakeConfigWithEmptyApiRuntime(
            webHttps: true,
            "api.example.com", "admin.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com");
        // …but the second host still ends up in the CORS list.
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://api.example.com");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://admin.example.com");
    }

    [Test]
    public async Task CorsListGetsOneEntryPerHost()
    {
        // CORS preserves all non-CIDR hosts: every leaf-eligible entry on the deployment side
        // gets a matching entry in the allow-list, in the same order, with the scheme derived
        // from WebHttps.
        var cfg = MakeConfigWithEmptyApiRuntime(
            webHttps: false,
            "api.example.com", "admin.example.com", "www.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(3);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[0]).IsEqualTo("http://api.example.com");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[1]).IsEqualTo("http://admin.example.com");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[2]).IsEqualTo("http://www.example.com");
    }

    [Test]
    public async Task StoredCallbackBaseUrlWinsOverDerived()
    {
        // The "stored wins" rule applied to CallbackBaseUrl: if the operator supplied an
        // explicit value (interactive or via the JSON), derivation must not overwrite it
        // — even if the value differs from what derivation would produce.
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.CallbackBaseUrl = "https://callback-override.example.com";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://callback-override.example.com");
        // JwtAuthority was empty so it still derives.
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com");
    }

    [Test]
    public async Task StoredJwtAuthorityWinsOverDerived()
    {
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.JwtAuthority = "https://issuer-override.example.com";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://issuer-override.example.com");
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com");
    }

    [Test]
    public async Task StoredCorsListWinsOverDerived()
    {
        // A non-empty CORS list (even with a single entry) must survive derivation untouched.
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.CorsAllowedOrigins = ["https://custom-front.example.com"];

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(1);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[0]).IsEqualTo("https://custom-front.example.com");
    }

    [Test]
    public async Task IsIdempotent()
    {
        // Calling ResolveDerivedDefaults twice must produce the same end state as calling it
        // once — once a value is filled, the second pass sees it as non-empty and skips it.
        // The interactive form's Show callbacks fire on every redraw, so this property is
        // load-bearing for menu stability.
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);
        var firstCallback = cfg.ApiRuntime.CallbackBaseUrl;
        var firstAuthority = cfg.ApiRuntime.JwtAuthority;
        var firstCorsCount = cfg.ApiRuntime.CorsAllowedOrigins.Count;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo(firstCallback);
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo(firstAuthority);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(firstCorsCount);
    }

    [Test]
    public async Task EmptyHostsLeavesEverythingUnset()
    {
        // ResolveDerivedDefaults has nothing to work from when Hosts is empty. Validate's
        // host check will throw downstream, but ResolveDerivedDefaults itself must early-out
        // gracefully rather than throwing or writing junk values.
        var cfg = MakeConfigWithEmptyApiRuntime();
        cfg.Deployment.Hosts = [];

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo(string.Empty);
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo(string.Empty);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DoesNotOverwriteJwtAudience()
    {
        // JwtAudience has a hardcoded property-initialiser default ("octocon") and is NOT a
        // derivable field — derivation must leave it alone in every path.
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.JwtAudience = "custom-aud";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.JwtAudience).IsEqualTo("custom-aud");
    }

    [Test]
    public async Task Ipv6PrimaryHostProducesBracketedUrl()
    {
        // RFC 3986 §3.2.2: a URI authority containing an IPv6 literal MUST bracket-wrap the
        // literal so the colons aren't interpreted as port separators. Pins the
        // HostParser.ToUrlHost behaviour as observed through the derivation surface that real
        // consumers (callback URL, JWT issuer) see.
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: "::1");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("http://[::1]");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("http://[::1]");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("http://[::1]");
    }

    [Test]
    public async Task CidrEntryIgnoredForUrlDerivation()
    {
        // CIDR entries widen the Name Constraints scope but have no URL form, so they must be
        // skipped when picking the primary host and when building the CORS allow-list.
        // ["192.168.1.0/24", "192.168.1.42"] → primary is 192.168.1.42; CORS has exactly one
        // entry (the CIDR doesn't add a second).
        var cfg = MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: ["192.168.1.0/24", "192.168.1.42"]);

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("http://192.168.1.42");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(1);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[0]).IsEqualTo("http://192.168.1.42");
    }
}
