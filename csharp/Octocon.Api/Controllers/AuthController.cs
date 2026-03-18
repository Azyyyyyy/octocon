using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Accounts;
using Octocon.Api;
using Octocon.Api.Services;
using System.Security.Cryptography;
using System.Text;

namespace Octocon.Api.Controllers;

[AllowAnonymous]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private const string MetadataCookieName = "octocon_auth_metadata";

    private readonly IAccountRepository _accounts;
    private readonly IConfiguration _configuration;
    private readonly ApiSettings _apiSettings;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly GoogleOAuthService _googleOAuth;

    public AuthController(
        IAccountRepository accounts,
        IConfiguration configuration,
        ApiSettings apiSettings,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth)
    {
        _accounts = accounts;
        _configuration = configuration;
        _apiSettings = apiSettings;
        _schemeProvider = schemeProvider;
        _googleOAuth = googleOAuth;
    }

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "google",
        "apple"
    };

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
        if (_apiSettings.AuthChallengeEnabled)
        {
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

                    Response.Headers["X-Octocon-OperationId"] = OperationIds.QueryAuthOAuthRequest;
                    return Challenge(props, challengeScheme);
                }
            }
        }

        // TODO(auth): Replace this fallback once provider challenge middleware is fully wired.
        // Until then we keep legacy request behavior (403 with empty body) and handle callback parity.
        Response.Headers["X-Octocon-OperationId"] = OperationIds.QueryAuthOAuthRequest;
        return StatusCode(StatusCodes.Status403Forbidden, string.Empty);
    }

    [HttpGet("{provider}/callback")]
    public Task<IActionResult> CallbackGet([FromRoute] string provider)
        => Callback(provider);

    [HttpPost("{provider}/callback")]
    public Task<IActionResult> CallbackPost([FromRoute] string provider)
        => Callback(provider);

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

        var token = IssueDeepLinkToken(systemId);
        var redirectBase = ResolveRedirectBase(provider);
        var redirectUrl = $"{redirectBase}?token={Uri.EscapeDataString(token)}&id={Uri.EscapeDataString(systemId)}";

        Response.Headers["X-Octocon-OperationId"] = OperationIds.AuthOAuthCallback;
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

        if (providerKey == "google" &&
            platform.Equals("wasm", StringComparison.OrdinalIgnoreCase) &&
            isBeta.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return _apiSettings.BetaFrontendAddress ?? throw new InvalidOperationException("Beta frontend address is not configured.");
        }

        if ((providerKey == "discord" || providerKey == "google") &&
            platform.Equals("wasm", StringComparison.OrdinalIgnoreCase))
        {
            return _apiSettings.FrontendAddress ?? throw new InvalidOperationException("Frontend address is not configured.");
        }

        return $"{_apiSettings.DeepEndpointAddress ?? throw new InvalidOperationException("Deep endpoint address is not configured.")}/auth/token";
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
            var redirectUri = BuildGoogleRedirectUri(provider);
            var email = await _googleOAuth.ExchangeCodeForEmailAsync(code, redirectUri, HttpContext.RequestAborted);
            
            return email ?? await GetValueAsync("email");
        }

        return await GetValueAsync("uid", "apple_id", "id");
    }

    private string BuildGoogleRedirectUri(string provider)
    {
        var providerKey = provider.ToLowerInvariant();
        var baseUrl = _apiSettings.AuthCallbackBaseUrl ?? $"{Request.Scheme}://{Request.Host}";
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

    private string IssueDeepLinkToken(string systemId)
    {
        var secret = _configuration["OCTOCON_AUTH_DEEP_LINK_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = _configuration["OCTOCON_JWT_AUTHORITY"] ?? "octocon-local";
        }

        var now = DateTimeOffset.UtcNow;

        var headerJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        });

        var payloadJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = _configuration["OCTOCON_JWT_AUTHORITY"] ?? "octocon-local",
            ["sub"] = systemId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["scope"] = "octocon:deeplink"
        });

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var encodedSignature = Base64UrlEncode(signature);

        // JWS Compact Serialization: base64url(header).base64url(payload).base64url(signature)
        return $"{signingInput}.{encodedSignature}";
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private string? GetChallengeScheme(string providerKey)
        => providerKey switch
        {
            "discord" => _apiSettings.AuthChallengeDiscordScheme,
            "google" => _apiSettings.AuthChallengeGoogleScheme,
            "apple" => _apiSettings.AuthChallengeAppleScheme,
            _ => null
        };

    private Dictionary<string, string>? GetChallengeParameters(string providerKey)
        => providerKey switch
        {
            "discord" => _apiSettings.AuthChallengeDiscordParameters,
            "google" => _apiSettings.AuthChallengeGoogleParameters,
            "apple" => _apiSettings.AuthChallengeAppleParameters,
            _ => null
        };

    private string BuildCallbackUri(string providerKey)
    {
        var callbackPath = $"/auth/{providerKey}/callback";
        
        if (!string.IsNullOrWhiteSpace(_apiSettings.AuthCallbackBaseUrl))
        {
            var baseUrl = _apiSettings.AuthCallbackBaseUrl.TrimEnd('/');
            return $"{baseUrl}{callbackPath}";
        }

        return callbackPath;
    }

    private static bool IsSupportedProvider(string provider)
        => !string.IsNullOrWhiteSpace(provider) && SupportedProviders.Contains(provider);
}
