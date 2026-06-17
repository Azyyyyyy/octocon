using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Interfold.Api.Auth;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;

namespace Interfold.Api.Controllers.Base;

public abstract class OAuthControllerBase : InterfoldControllerBase
{
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "google",
        "apple"
    };

    protected readonly IOptionsMonitor<AuthenticationConfiguration> AuthOptions;
    protected readonly IAuthenticationSchemeProvider SchemeProvider;
    protected readonly GoogleOAuthService GoogleOAuth;
    protected readonly DiscordOAuthService DiscordOAuth;
    protected readonly AppleOAuthService AppleOAuth;

    protected OAuthControllerBase(
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth)
    {
        AuthOptions = authOptions;
        SchemeProvider = schemeProvider;
        GoogleOAuth = googleOAuth;
        DiscordOAuth = discordOAuth;
        AppleOAuth = appleOAuth;
    }

    protected abstract string CallbackRoutePrefix { get; }

    protected static bool IsSupportedProvider(string provider)
        => !string.IsNullOrWhiteSpace(provider) && SupportedProviders.Contains(provider);

    protected async Task<string?> ExtractProviderIdentityAsync(string provider)
    {
        if (provider.Equals("discord", StringComparison.OrdinalIgnoreCase))
        {
            var code = await GetValueAsync("code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                var redirectUri = BuildCallbackBaseUri(provider);
                return await DiscordOAuth.ExchangeCodeForDiscordIdAsync(code, redirectUri, HttpContext.RequestAborted);
            }

            return await GetValueAsync("uid", "discord_id", "id");
        }

        if (provider.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            var code = await GetValueAsync("code");
            if (string.IsNullOrWhiteSpace(code))
            {
                return await GetValueAsync("email");
            }

            var redirectUri = BuildCallbackBaseUri(provider);
            var email = await GoogleOAuth.ExchangeCodeForEmailAsync(code, redirectUri, HttpContext.RequestAborted);

            return email ?? await GetValueAsync("email");
        }

        if (provider.Equals("apple", StringComparison.OrdinalIgnoreCase))
        {
            var code = await GetValueAsync("code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                var redirectUri = BuildCallbackBaseUri(provider);
                var appleId = await AppleOAuth.ExchangeCodeForAppleIdAsync(code, redirectUri, HttpContext.RequestAborted);
                if (!string.IsNullOrWhiteSpace(appleId))
                {
                    return appleId;
                }
            }

            var idToken = await GetValueAsync("id_token");
            var sub = AppleOAuth.ExtractSubFromJwt(idToken);
            if (!string.IsNullOrWhiteSpace(sub))
            {
                return sub;
            }

            return await GetValueAsync("uid", "apple_id", "id", "sub");
        }

        return null;
    }

    protected string BuildCallbackBaseUri(string provider)
    {
        var providerKey = provider.ToLowerInvariant();
        var authConfig = AuthOptions.CurrentValue;
        var baseUrl = authConfig.CallbackBaseUrl ?? $"{Request.Scheme}://{Request.Host}";
        return $"{baseUrl}/{CallbackRoutePrefix}/{providerKey}/callback";
    }

    protected async Task<string?> GetValueAsync(params string[] keys)
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

    protected static string? GetChallengeScheme(string providerKey)
    {
        return providerKey switch
        {
            "discord" => OAuthChallengeServiceCollectionExtensions.DiscordSchemeName,
            "google" => OAuthChallengeServiceCollectionExtensions.GoogleSchemeName,
            "apple" => OAuthChallengeServiceCollectionExtensions.AppleSchemeName,
            _ => null
        };
    }

    protected void StoreRedirectUriCookie(string cookieName)
    {
        var redirectUri = Request.Query["redirect_uri"].ToString();
        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            Response.Cookies.Append(cookieName, redirectUri, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10)
            });
        }
    }

    protected void StoreQueryCookie(string cookieName, string queryKey)
    {
        var value = Request.Query[queryKey].ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            Response.Cookies.Append(cookieName, value, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10)
            });
        }
    }

    protected IActionResult UnsupportedProviderResponse(string provider)
    {
        return BadRequest(new
        {
            error = "Unsupported OAuth provider.",
            code = "invalid_oauth_provider",
            provider
        });
    }

    /// <summary>
    /// Looks up the registered challenge scheme for the supplied provider key and returns a
    /// <see cref="ChallengeResult"/> against it, or <c>null</c> when the scheme isn't
    /// registered (typically because the operator hasn't supplied the matching
    /// <c>OCTOCON_*_OAUTH_CLIENT_ID</c>). The static challenge query parameters and the
    /// authorization endpoint URL both come from <see cref="OAuthChallengeServiceCollectionExtensions"/>
    /// — see that type for why they're baked in rather than threaded through here.
    /// </summary>
    protected async Task<IActionResult?> IssueChallengeIfRegisteredAsync(
        string providerKey,
        string operationId)
    {
        var challengeScheme = GetChallengeScheme(providerKey);
        if (string.IsNullOrWhiteSpace(challengeScheme))
            return null;

        var registeredScheme = await SchemeProvider.GetSchemeAsync(challengeScheme);
        if (registeredScheme is null)
            return null;

        var props = new AuthenticationProperties
        {
            RedirectUri = BuildCallbackBaseUri(providerKey)
        };

        Response.Headers["X-Interfold-OperationId"] = operationId;
        return Challenge(props, challengeScheme);
    }
}
