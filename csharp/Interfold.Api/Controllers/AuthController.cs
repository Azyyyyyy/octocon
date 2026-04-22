using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Accounts;
using Interfold.Domain.Auth;
using Interfold.Api.Services;
using Interfold.Infrastructure.Configuration;
using System.Security.Cryptography;
using System.Text;
using Interfold.Infrastructure;

namespace Interfold.Api.Controllers;

[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private const string MetadataCookieName = "octocon_auth_metadata";

    private readonly IAccountRepository _accounts;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;
    private readonly IOptionsMonitor<ApiConfiguration> _apiOptions;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly GoogleOAuthService _googleOAuth;
    private readonly DiscordOAuthService _discordOAuth;
    private readonly AppleOAuthService _appleOAuth;
    private readonly IAuthTokenRevocationRepository _tokenRevocation;

    public AuthController(
        IAccountRepository accounts,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IOptionsMonitor<ApiConfiguration> apiOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        IAuthTokenRevocationRepository tokenRevocation)
    {
        _accounts = accounts;
        _authOptions = authOptions;
        _apiOptions = apiOptions;
        _schemeProvider = schemeProvider;
        _googleOAuth = googleOAuth;
        _discordOAuth = discordOAuth;
        _appleOAuth = appleOAuth;
        _tokenRevocation = tokenRevocation;
    }

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "google",
        "apple"
    };

    [AllowAnonymous]
    [HttpGet("{provider}")]
    public async Task<IActionResult> BeginOAuth([FromRoute] string provider)
    {
        if (!IsSupportedProvider(provider))
        {
            return BadRequest(new
            {
                error = "Unsupported OAuth provider.",
                code = "invalid_oauth_provider",
                provider
            });
        }

        var metadata = BuildMetadataJson();
        Response.Cookies.Append(MetadataCookieName, metadata, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var providerKey = provider.ToLowerInvariant();
        var challengeScheme = GetChallengeScheme(providerKey);
        if (!string.IsNullOrWhiteSpace(challengeScheme))
        {
            var registeredScheme = await _schemeProvider.GetSchemeAsync(challengeScheme);
            if (registeredScheme is not null)
            {
                var redirectUri = BuildCallbackUri(providerKey);
                var props = new AuthenticationProperties
                {
                    RedirectUri = redirectUri
                };

                var additionalParameters = GetChallengeParameters(providerKey);
                if (additionalParameters?.Count > 0)
                {
                    foreach (var (key, value) in additionalParameters)
                    {
                        props.Items[key] = value;
                    }
                }

                Response.Headers["X-Interfold-OperationId"] = OperationIds.QueryAuthOAuthRequest;
                return Challenge(props, challengeScheme);
            }
        }

        Response.Headers["X-Interfold-OperationId"] = OperationIds.QueryAuthOAuthRequest;
        return StatusCode(StatusCodes.Status403Forbidden, string.Empty);
    }

    [AllowAnonymous]
    [HttpGet("{provider}/callback")]
    public Task<IActionResult> CallbackGet([FromRoute] string provider)
        => Callback(provider);

    [AllowAnonymous]
    [HttpPost("{provider}/callback")]
    public Task<IActionResult> CallbackPost([FromRoute] string provider)
        => Callback(provider);

    //TODO: To ensure route works as expected
    /// <summary>
    /// Revokes the current authenticated token (logout).
    /// Requires authentication. The JTI claim from the current token is extracted and marked as revoked.
    /// After revocation, the token cannot be used for subsequent requests.
    /// </summary>
    [Authorize]
    [HttpPost("revoke")]
    public async Task<IActionResult> RevokeToken()
    {
        var jti = User.FindFirst("jti")?.Value;
        if (string.IsNullOrWhiteSpace(jti))
        {
            return BadRequest(new
            {
                error = "Token is missing JTI claim.",
                code = "invalid_token"
            });
        }

        await _tokenRevocation.RevokeTokenAsync(jti, HttpContext.RequestAborted);

        Response.Headers["X-Interfold-OperationId"] = OperationIds.AuthRevokeToken;
        return NoContent();
    }

    private async Task<IActionResult> Callback(string provider)
    {
        if (!IsSupportedProvider(provider))
        {
            return BadRequest(new
            {
                error = "Unsupported OAuth provider.",
                code = "invalid_oauth_provider",
                provider
            });
        }

        var providerKey = provider.ToLowerInvariant();

        var identity = await ExtractProviderIdentityAsync(provider);
        if (string.IsNullOrWhiteSpace(identity))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var systemId = providerKey switch
        {
            "discord" => await _accounts.FindSystemIdByDiscordIdAsync(identity, HttpContext.RequestAborted),
            "google" => await _accounts.FindSystemIdByEmailAsync(identity, HttpContext.RequestAborted),
            "apple" => await _accounts.FindSystemIdByAppleIdAsync(identity, HttpContext.RequestAborted),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(systemId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you use the same account to sign in before?");
        }

        var token = await IssueDeepLinkTokenAsync(systemId);
        var redirectBase = ResolveRedirectBase(provider);
        var redirectUrl = $"{redirectBase}?token={Uri.EscapeDataString(token)}&id={Uri.EscapeDataString(systemId)}";

        Response.Headers["X-Interfold-OperationId"] = OperationIds.AuthOAuthCallback;
        return Redirect(redirectUrl);
    }

    private string ResolveRedirectBase(string provider)
    {
        var providerKey = provider.ToLowerInvariant();
        string? metadataJson = Request.Cookies[MetadataCookieName];
        if (Request.Query.TryGetValue("platform", out var platformOverride))
        {
            metadataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["platform"] = platformOverride.ToString(),
                ["is_beta"] = Request.Query["is_beta"].ToString()
            });
        }

        var platform = "unknown";
        var isBeta = "false";

        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("platform", out var p))
                    platform = p.GetString() ?? platform;
                if (doc.RootElement.TryGetProperty("is_beta", out var b))
                    isBeta = b.GetString() ?? isBeta;
            }
            catch (System.Text.Json.JsonException)
            {
                // Keep safe defaults if metadata cannot be parsed.
            }
        }

        var apiConfig = _apiOptions.CurrentValue;
        if (providerKey == "google" &&
            platform.Equals("wasm", StringComparison.OrdinalIgnoreCase) &&
            isBeta.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return apiConfig.BetaFrontendAddress ?? throw new InvalidOperationException("Beta frontend address is not configured.");
        }

        if ((providerKey == "discord" || providerKey == "google") &&
            platform.Equals("wasm", StringComparison.OrdinalIgnoreCase))
        {
            return apiConfig.FrontendAddress ?? throw new InvalidOperationException("Frontend address is not configured.");
        }

        return BuildDeepAuthRedirectBase(apiConfig.DeepLinkAddress);
    }

    /// <summary>
    /// Builds a deep-link auth callback URL base with legacy compatibility.
    /// Legacy behavior expects https://octocon.app/deep/auth/token.
    /// </summary>
    private static string BuildDeepAuthRedirectBase(string? deepLinkAddress)
    {
        var normalizedBase = NormalizeDeepLinkBase(deepLinkAddress);

        if (normalizedBase.EndsWith("/auth/token", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedBase;
        }

        if (normalizedBase.EndsWith("/deep", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalizedBase}/auth/token";
        }

        return $"{normalizedBase}/deep/auth/token";
    }

    private static string NormalizeDeepLinkBase(string? deepLinkAddress)
    {
        if (string.IsNullOrWhiteSpace(deepLinkAddress))
        {
            return "https://octocon.app/deep";
        }

        return deepLinkAddress.Trim().TrimEnd('/');
    }

    private string BuildMetadataJson()
    {
        var platform = Request.Query["platform"].ToString();
        var versionCode = Request.Query["version_code"].ToString();
        var isBeta = Request.Query["is_beta"].ToString();

        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["platform"] = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform,
            ["version_code"] = string.IsNullOrWhiteSpace(versionCode) ? "unknown" : versionCode,
            ["is_beta"] = string.IsNullOrWhiteSpace(isBeta) ? "false" : isBeta
        });
    }

    private async Task<string?> ExtractProviderIdentityAsync(string provider)
    {
        if (provider.Equals("discord", StringComparison.OrdinalIgnoreCase))
        {
            // Discord OAuth2 callback delivers an authorization code; exchange it for the user ID.
            var code = await GetValueAsync("code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                var redirectUri = BuildCallbackBaseUri(provider);
                return await _discordOAuth.ExchangeCodeForDiscordIdAsync(code, redirectUri, HttpContext.RequestAborted);
            }

            // Fallback for legacy flows that deliver the ID directly.
            return await GetValueAsync("uid", "discord_id", "id");
        }

        if (provider.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            // Google OAuth2 callback sends 'code' instead of 'email'.
            // Exchange code for access token and fetch user info.
            var code = await GetValueAsync("code");
            if (string.IsNullOrWhiteSpace(code))
            {
                // Fallback: try direct email parameter for legacy flows
                return await GetValueAsync("email");
            }

            // Build the redirect URI for token exchange
            var redirectUri = BuildCallbackBaseUri(provider);
            var email = await _googleOAuth.ExchangeCodeForEmailAsync(code, redirectUri, HttpContext.RequestAborted);

            return email ?? await GetValueAsync("email");
        }

        //TODO: See if this actually works, we're currently not enrolled into Apple's program so can't test the full flow.
        if (provider.Equals("apple", StringComparison.OrdinalIgnoreCase))
        {
            var code = await GetValueAsync("code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                var redirectUri = BuildCallbackBaseUri(provider);
                var appleId = await _appleOAuth.ExchangeCodeForAppleIdAsync(code, redirectUri, HttpContext.RequestAborted);
                if (!string.IsNullOrWhiteSpace(appleId))
                {
                    return appleId;
                }
            }

            var idToken = await GetValueAsync("id_token");
            var sub = _appleOAuth.ExtractSubFromJwt(idToken);
            if (!string.IsNullOrWhiteSpace(sub))
            {
                return sub;
            }

            return await GetValueAsync("uid", "apple_id", "id", "sub");
        }

        return null;
    }

    private string BuildCallbackBaseUri(string provider)
    {
        var providerKey = provider.ToLowerInvariant();
        var authConfig = _authOptions.CurrentValue;
        var baseUrl = authConfig.CallbackBaseUrl ?? $"{Request.Scheme}://{Request.Host}";
        return $"{baseUrl}/auth/{providerKey}/callback";
    }

    private async Task<string?> GetValueAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            var query = Request.Query[key].ToString();
            if (!string.IsNullOrWhiteSpace(query))
            {
                return query;
            }
        }

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
            foreach (var key in keys)
            {
                var value = form[key].ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private async Task<string> IssueDeepLinkTokenAsync(string systemId)
    {
        var authConfig = _authOptions.CurrentValue;
        var jti = Guid.NewGuid().ToString("N");

        // Set expiry to 100 years in the future. This is practically permanent
        // but avoids DateTimeOffset.MaxValue which can cause int64 overflow on validation.
        // If a token is compromised, it can be revoked explicitly via POST /auth/revoke.
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddYears(100);

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, jti, systemId);
        
        // Record the issued token for revocation tracking
        await _tokenRevocation.RecordTokenAsync(jti, systemId, expiresAt, HttpContext.RequestAborted);

        // JWS Compact Serialization: base64url(header).base64url(payload).base64url(signature)
        return token;
    }
    
    private string? GetChallengeScheme(string providerKey)
    {
        var authConfig = _authOptions.CurrentValue;
        return providerKey switch
        {
            "discord" => authConfig.DiscordSchemeName,
            "google" => authConfig.GoogleSchemeName,
            "apple" => authConfig.AppleSchemeName,
            _ => null
        };
    }

    private Dictionary<string, string>? GetChallengeParameters(string providerKey)
    {
        var authConfig = _authOptions.CurrentValue;
        return providerKey switch
        {
            "discord" => authConfig.DiscordParameters,
            "google" => authConfig.GoogleParameters,
            "apple" => authConfig.AppleParameters,
            _ => null
        };
    }

    private string BuildCallbackUri(string providerKey)
    {
        var callbackPath = $"/auth/{providerKey}/callback";
        var authConfig = _authOptions.CurrentValue;
        
        if (!string.IsNullOrWhiteSpace(authConfig.CallbackBaseUrl))
        {
            var baseUrl = authConfig.CallbackBaseUrl.TrimEnd('/');
            return $"{baseUrl}{callbackPath}";
        }

        return callbackPath;
    }

    private static bool IsSupportedProvider(string provider)
        => !string.IsNullOrWhiteSpace(provider) && SupportedProviders.Contains(provider);
}
