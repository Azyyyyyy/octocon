namespace Interfold.Contracts.Configuration;

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
    /// Deep link JWT signing secret (phase F token exchange).
    /// Env: OCTOCON_AUTH_DEEP_LINK_SECRET
    /// </summary>
    public string? DeepLinkSecret { get; set; }

    /// <summary>
    /// JWT authority fallback
    /// Env: OCTOCON_JWT_AUTHORITY
    /// </summary>
    public string JwtAuthority { get; set; } = "";

    /// <summary>
    /// JWT audience
    /// Env: OCTOCON_JWT_AUDIENCE
    /// </summary>
    public string JwtAudience { get; set; } = "octocon";

    /// <summary>
    /// ES256 private key (PEM) used for token issuance.
    /// Env: OCTOCON_AUTH_EC_PRIVATE_KEY_PEM
    /// </summary>
    public string? JwtEs256PrivateKeyPem { get; set; }

    /// <summary>
    /// ES256 private key file path (PEM).
    /// If ES256 is enabled and this file does not exist, it can be created automatically.
    /// Env: OCTOCON_AUTH_EC_PRIVATE_KEY_FILE
    /// </summary>
    public string? JwtEs256PrivateKeyFile { get; set; }

    /// <summary>
    /// ES256 public key file path (PEM).
    /// If ES256 is enabled and this file does not exist, it can be created automatically.
    /// Env: OCTOCON_AUTH_EC_PUBLIC_KEY_FILE
    /// </summary>
    public string? JwtEs256PublicKeyFile { get; set; }

    /// <summary>
    /// ES256 public/private verification keys (PEM) used for token validation.
    /// Env: OCTOCON_AUTH_EC_PUBLIC_KEY_PEM, OCTOCON_AUTH_EC_PUBLIC_KEYS
    /// </summary>
    public string[]? JwtEs256VerificationKeyPems { get; set; }

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

    /// <summary>
    /// Apple OAuth 2.0 client ID for backend token exchange.
    /// Env: OCTOCON_APPLE_OAUTH_CLIENT_ID
    /// </summary>
    public string? AppleOAuthClientId { get; set; }

    /// <summary>
    /// Apple OAuth 2.0 client secret for token exchange.
    /// Env: OCTOCON_APPLE_OAUTH_CLIENT_SECRET
    /// </summary>
    public string? AppleOAuthClientSecret { get; set; }

    /// <summary>
    /// Discord OAuth 2.0 client ID for backend token exchange.
    /// Env: OCTOCON_DISCORD_OAUTH_CLIENT_ID
    /// </summary>
    public string? DiscordOAuthClientId { get; set; }

    /// <summary>
    /// Discord OAuth 2.0 client secret for token exchange.
    /// Env: OCTOCON_DISCORD_OAUTH_CLIENT_SECRET
    /// </summary>
    public string? DiscordOAuthClientSecret { get; set; }

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
