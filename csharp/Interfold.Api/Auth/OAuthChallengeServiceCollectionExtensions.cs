using Interfold.Infrastructure.Configuration;

namespace Interfold.Api.Auth;

/// <summary>
/// Extension methods for registering OAuth challenge redirect schemes from strongly-typed configuration.
/// </summary>
internal static class OAuthChallengeServiceCollectionExtensions
{
    /// <summary>
    /// Reads OAuth challenge scheme configuration from environment variables and registers
    /// each scheme whose endpoint is configured (Discord, Google, Apple).
    /// </summary>
    public static IServiceCollection AddInterfoldAuthChallengeSchemes(
        this IServiceCollection services,
        IConfiguration config)
    {
        var authConfig = config.BindAuthenticationConfiguration();

        TryAddScheme(services, authConfig.DiscordSchemeName, authConfig.DiscordEndpoint, authConfig.DiscordParameters);
        TryAddScheme(services, authConfig.GoogleSchemeName,  authConfig.GoogleEndpoint,  authConfig.GoogleParameters);
        TryAddScheme(services, authConfig.AppleSchemeName,   authConfig.AppleEndpoint,   authConfig.AppleParameters);

        return services;
    }

    private static void TryAddScheme(
        IServiceCollection services,
        string scheme,
        string? endpoint,
        Dictionary<string, string>? additionalParameters = null)
    {
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(endpoint))
            return;

        services
            .AddAuthentication()
            .AddScheme<RedirectChallengeOptions, RedirectChallengeAuthenticationHandler>(scheme, options =>
            {
                options.AuthorizationEndpoint = endpoint;
                options.AdditionalParameters = additionalParameters;
            });
    }
}
