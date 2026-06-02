using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure;
using Interfold.Api.Controllers.Base;

namespace Interfold.Api.Controllers;

[Route("auth")]
public sealed class AuthController : OAuthControllerBase
{
    private const string MetadataCookieName = "octocon_auth_metadata";
    private const string RedirectUriCookieName = "octocon_auth_redirect_uri";

    private readonly IAccountRepository _accounts;
    private readonly IAuthTokenRevocationRepository _tokenRevocation;
    private readonly IEncryptionStateRepository _encryptionRepository;

    public AuthController(
        IAccountRepository accounts,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IOptionsMonitor<ApiConfiguration> apiOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        IAuthTokenRevocationRepository tokenRevocation,
        IEncryptionStateRepository encryptionRepository)
        : base(authOptions, apiOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _accounts = accounts;
        _tokenRevocation = tokenRevocation;
        _encryptionRepository = encryptionRepository;
    }

    protected override string CallbackRoutePrefix => "auth";

    [AllowAnonymous]
    [HttpGet("{provider}")]
    public async Task<IActionResult> Begin([FromRoute] string provider)
    {
        if (!IsSupportedProvider(provider))
            return UnsupportedProviderResponse(provider);

        var metadata = BuildMetadataJson();
        Response.Cookies.Append(MetadataCookieName, metadata, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        StoreRedirectUriCookie(RedirectUriCookieName);

        var providerKey = provider.ToLowerInvariant();
        var challenge = await IssueChallengeIfRegisteredAsync(
            providerKey, OperationIds.QueryAuthOAuthRequest, GetChallengeParameters(providerKey));

        if (challenge is not null)
            return challenge;

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
            return UnsupportedProviderResponse(provider);

        var identity = await ExtractProviderIdentityAsync(provider);
        if (string.IsNullOrWhiteSpace(identity))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var providerKey = provider.ToLowerInvariant();
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

        var encryptionState = await _encryptionRepository.GetAsync(systemId, HttpContext.RequestAborted);
        if (encryptionState?.Salt == null)
        {
            // Generate per-user salt (32 random bytes, Base64-encoded)
            var saltBytes = RandomNumberGenerator.GetBytes(32);
            var salt = Convert.ToBase64String(saltBytes);

            await _encryptionRepository.UpsertAsync(systemId, false, null, salt, HttpContext.RequestAborted);
        }

        var token = await IssueDeepLinkTokenAsync(systemId);

        var clientRedirectUri = Request.Cookies[RedirectUriCookieName];
        Response.Cookies.Delete(RedirectUriCookieName);

        string redirectUrl;
        if (!string.IsNullOrWhiteSpace(clientRedirectUri))
        {
            var separator = clientRedirectUri.Contains('?') ? '&' : '?';
            redirectUrl = $"{clientRedirectUri}{separator}token={Uri.EscapeDataString(token)}&id={Uri.EscapeDataString(systemId)}";
        }
        else
        {
            var redirectBase = ResolveRedirectBase();
            redirectUrl = $"{redirectBase}?token={Uri.EscapeDataString(token)}&id={Uri.EscapeDataString(systemId)}";
        }

        Response.Headers["X-Interfold-OperationId"] = OperationIds.AuthOAuthCallback;
        return Redirect(redirectUrl);
    }

    private string ResolveRedirectBase()
    {
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

        var apiConfig = ApiOptions.CurrentValue;

        if (platform.Equals("wasm", StringComparison.OrdinalIgnoreCase))
        {
            if (isBeta.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return apiConfig.BetaFrontendAddress ?? throw new InvalidOperationException("Beta frontend address is not configured.");
            }
    
            return apiConfig.FrontendAddress ?? throw new InvalidOperationException("Frontend address is not configured.");
        }
        
        if (platform.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            return "octocon://deep/auth/token";
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

    private async Task<string> IssueDeepLinkTokenAsync(string systemId)
    {
        var authConfig = AuthOptions.CurrentValue;
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
    
}
