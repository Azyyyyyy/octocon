namespace Interfold.Api;

public sealed class ApiSettings
{
    // Optional scaffold: when true, /auth and /auth/link request endpoints may issue
    // Challenge(...) if the configured scheme exists at runtime.
    public bool AuthChallengeEnabled { get; init; }

    public string AuthChallengeDiscordScheme { get; init; } = "oauth-discord";
    public string AuthChallengeGoogleScheme { get; init; } = "oauth-google";
    public string AuthChallengeAppleScheme { get; init; } = "oauth-apple";

    public Dictionary<string, string>? AuthChallengeDiscordParameters { get; init; }
    public Dictionary<string, string>? AuthChallengeGoogleParameters { get; init; }
    public Dictionary<string, string>? AuthChallengeAppleParameters { get; init; }

    public string? AuthCallbackBaseUrl { get; init; }

    // Google OAuth2 credentials for backend token exchange in callback handlers
    public string? GoogleOAuthClientId { get; init; }
    public string? GoogleOAuthClientSecret { get; init; }

    public string? FrontendAddress { get; init; }
    public string? BetaFrontendAddress { get; init; }
    public string? DeepEndpointAddress { get; init; }
}
