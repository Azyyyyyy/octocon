using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Octocon.Api.Socket;
using Octocon.Domain.Abstractions;
using Octocon.Contracts.Operations;
using Octocon.Domain.Accounts;
using Octocon.Api;
using Octocon.Api.Services;

namespace Octocon.Api.Controllers;

[AllowAnonymous]
[Route("auth/link")]
public sealed class AuthLinkController : ControllerBase
{
    private const string LinkTokenCookieName = "octocon_link_token";

    private readonly IAccountRepository _accounts;
    private readonly ApiSettings _apiSettings;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly GoogleOAuthService _googleOAuth;
    private readonly IClusterEventBus _eventBus;

    public AuthLinkController(
        IAccountRepository accounts,
        ApiSettings apiSettings,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        IClusterEventBus eventBus)
    {
        _accounts = accounts;
        _apiSettings = apiSettings;
        _schemeProvider = schemeProvider;
        _googleOAuth = googleOAuth;
        _eventBus = eventBus;
    }

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "google",
        "apple"
    };

    [HttpGet("{provider}")]
    public async Task<IActionResult> BeginLink([FromRoute] string provider)
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

        var linkToken = Request.Query["link_token"].ToString();
        if (!string.IsNullOrWhiteSpace(linkToken))
        {
            Response.Cookies.Append(LinkTokenCookieName, linkToken, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10)
            });
        }

        var providerKey = provider.ToLowerInvariant();
        if (_apiSettings.AuthChallengeEnabled)
        {
            var challengeScheme = GetChallengeScheme(providerKey);
            if (!string.IsNullOrWhiteSpace(challengeScheme))
            {
                var registeredScheme = await _schemeProvider.GetSchemeAsync(challengeScheme);
                if (registeredScheme is not null)
                {
                    var props = new AuthenticationProperties
                    {
                        RedirectUri = $"/auth/link/{providerKey}/callback"
                    };

                    Response.Headers["X-Octocon-OperationId"] = OperationIds.QueryAuthLinkRequest;
                    return Challenge(props, challengeScheme);
                }
            }
        }

        // Google OAuth2 token exchange is now handled in ExtractProviderIdentityAsync.
        // If no OAuth provider is configured or challenge disabled, return 403 fallback.
        Response.Headers["X-Octocon-OperationId"] = OperationIds.QueryAuthLinkRequest;
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

        var linkToken = await GetValueAsync("link_token") ?? Request.Cookies[LinkTokenCookieName];
        if (string.IsNullOrWhiteSpace(linkToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        var systemId = await _accounts.ResolveSystemIdByLinkTokenAsync(linkToken, HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            Response.Cookies.Delete(LinkTokenCookieName);
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        await _accounts.ClearLinkTokenAsync(systemId, HttpContext.RequestAborted);
        Response.Cookies.Delete(LinkTokenCookieName);

        var identity = await ExtractProviderIdentityAsync(provider);
        if (string.IsNullOrWhiteSpace(identity))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var result = providerKey switch
        {
            "discord" => await _accounts.LinkDiscordToUserAsync(systemId, identity, HttpContext.RequestAborted),
            "google" => await _accounts.LinkEmailToUserAsync(systemId, identity, HttpContext.RequestAborted),
            "apple" => await _accounts.LinkAppleToUserAsync(systemId, identity, HttpContext.RequestAborted),
            _ => AccountLinkResult.UserNotFound
        };

        Response.Headers["X-Octocon-OperationId"] = OperationIds.AuthLinkCallback;

        return result switch
        {
            AccountLinkResult.Success => await RedirectWithSocketEventAsync(systemId, providerKey, identity),
            AccountLinkResult.AlreadyLinked => StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = providerKey switch
                {
                    "discord" => "A Discord account is already linked to this account; please unlink it first.",
                    "google" => "A Google account is already linked to this account; please unlink it first.",
                    "apple" => "An Apple account is already linked to this account; please unlink it first.",
                    _ => "An account is already linked to this account; please unlink it first."
                }
            }),
            AccountLinkResult.UserExists => StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = providerKey switch
                {
                    "discord" => "This Discord account is already linked to another account.",
                    "google" => "This email address is already linked to another account.",
                    "apple" => "This Apple account is already linked to another account.",
                    _ => "This account is already linked to another account."
                }
            }),
            _ => StatusCode(StatusCodes.Status403Forbidden, new { error = "System not found" })
        };
    }

    private async Task<IActionResult> RedirectWithSocketEventAsync(string systemId, string providerKey, string identity)
    {
        var eventName = providerKey switch
        {
            "discord" => "discord_account_linked",
            "google" => "google_account_linked",
            "apple" => "apple_account_linked",
            _ => "account_linked"
        };

        object payload = providerKey switch
        {
            "discord" => new { discord_id = identity },
            "google" => new { email = identity },
            "apple" => new { apple_id = identity },
            _ => new { }
        };

        await _eventBus.PublishAsync(new SocketRawPushEvent(
            systemId,
            eventName,
            System.Text.Json.JsonSerializer.Serialize(payload)), HttpContext.RequestAborted);

        return Redirect($"{_apiSettings.DeepEndpointAddress}/link_success/{providerKey}");
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
        return $"{baseUrl}/auth/link/{providerKey}/callback";
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

    private string? GetChallengeScheme(string providerKey)
        => providerKey switch
        {
            "discord" => _apiSettings.AuthChallengeDiscordScheme,
            "google" => _apiSettings.AuthChallengeGoogleScheme,
            "apple" => _apiSettings.AuthChallengeAppleScheme,
            _ => null
        };

    private static bool IsSupportedProvider(string provider)
        => !string.IsNullOrWhiteSpace(provider) && SupportedProviders.Contains(provider);
}
