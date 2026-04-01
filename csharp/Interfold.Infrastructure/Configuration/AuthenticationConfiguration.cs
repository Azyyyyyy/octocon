namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// Authentication, OAuth, and JWT configuration.
/// Binds from environment variables with OCTOCON_ and GUARDIAN_ prefixes.
/// </summary>
public sealed class AuthenticationConfiguration
{
    public const string SectionName = "Octocon:Authentication";

    /// <summary>
    /// Base URL for OAuth callback handlers.
    /// Env: OCTOCON_AUTH_CALLBACK_BASE_URL
    /// </summary>
    public string? CallbackBaseUrl { get; set; }

    /// <summary>
    /// JWT signing secrets for token validation and issuance.
    /// Checked in order: OCTOCON_AUTH_DEEP_LINK_SECRET, GUARDIAN_SECRET_KEY,
    /// OCTOCON_JWT_AUTHORITY (legacy), then 'octocon-local' (development fallback).
    /// </summary>
    public string[]? JwtSigningSecrets { get; set; }

    /// <summary>
    /// Deep link JWT signing secret (phase F token exchange).
    /// Env: OCTOCON_AUTH_DEEP_LINK_SECRET
    /// </summary>
    public string? DeepLinkSecret { get; set; }

    /// <summary>
    /// Legacy JWT authority fallback (deprecated, kept for backward compatibility).
    /// Env: OCTOCON_JWT_AUTHORITY
    /// </summary>
    public string? JwtAuthority { get; set; }

    /// <summary>
    /// Google OAuth 2.0 client ID for backend token exchange.
    /// Env: OCTOCON_GOOGLE_OAUTH_CLIENT_ID
    /// </summary>
    public string? GoogleOAuthClientId { get; set; }

    /// <summary>
    /// Google OAuth 2.0 client secret for token exchange.
    /// Env: OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET
    /// </summary>
    public string? GoogleOAuthClientSecret { get; set; }

    // --- Discord OAuth Challenge ----

    /// <summary>
    /// Discord OAuth challenge scheme name.
    /// Env: OCTOCON_AUTH_CHALLENGE_DISCORD_SCHEME
    /// Default: 'oauth-discord'
    /// </summary>
    public string DiscordSchemeName { get; set; } = "oauth-discord";

    /// <summary>
    /// Discord OAuth provider authorization endpoint.
    /// Env: OCTOCON_AUTH_CHALLENGE_DISCORD_ENDPOINT
    /// </summary>
    public string? DiscordEndpoint { get; set; }

    /// <summary>
    /// Discord OAuth additional parameters (parsed from OCTOCON_AUTH_CHALLENGE_DISCORD_PARAMS).
    /// </summary>
    public Dictionary<string, string>? DiscordParameters { get; set; }

    // --- Google OAuth Challenge ----

    /// <summary>
    /// Google OAuth challenge scheme name.
    /// Env: OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME
    /// Default: 'oauth-google'
    /// </summary>
    public string GoogleSchemeName { get; set; } = "oauth-google";

    /// <summary>
    /// Google OAuth provider authorization endpoint.
    /// Env: OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT
    /// </summary>
    public string? GoogleEndpoint { get; set; }

    /// <summary>
    /// Google OAuth additional parameters (parsed from OCTOCON_AUTH_CHALLENGE_GOOGLE_PARAMS).
    /// </summary>
    public Dictionary<string, string>? GoogleParameters { get; set; }

    // --- Apple OAuth Challenge ----

    /// <summary>
    /// Apple OAuth challenge scheme name.
    /// Env: OCTOCON_AUTH_CHALLENGE_APPLE_SCHEME
    /// Default: 'oauth-apple'
    /// </summary>
    public string AppleSchemeName { get; set; } = "oauth-apple";

    /// <summary>
    /// Apple OAuth provider authorization endpoint.
    /// Env: OCTOCON_AUTH_CHALLENGE_APPLE_ENDPOINT
    /// </summary>
    public string? AppleEndpoint { get; set; }

    /// <summary>
    /// Apple OAuth additional parameters (parsed from OCTOCON_AUTH_CHALLENGE_APPLE_PARAMS).
    /// </summary>
    public Dictionary<string, string>? AppleParameters { get; set; }
}
