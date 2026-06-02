using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
    protected readonly IOptionsMonitor<ApiConfiguration> ApiOptions;
    protected readonly IAuthenticationSchemeProvider SchemeProvider;
    protected readonly GoogleOAuthService GoogleOAuth;
    protected readonly DiscordOAuthService DiscordOAuth;
    protected readonly AppleOAuthService AppleOAuth;

    protected OAuthControllerBase(
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IOptionsMonitor<ApiConfiguration> apiOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth)
    {
        AuthOptions = authOptions;
        ApiOptions = apiOptions;
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

    protected string? GetChallengeScheme(string providerKey)
    {
        var authConfig = AuthOptions.CurrentValue;
        return providerKey switch
        {
            "discord" => authConfig.DiscordSchemeName,
            "google" => authConfig.GoogleSchemeName,
            "apple" => authConfig.AppleSchemeName,
            _ => null
        };
    }

    protected static string NormalizeDeepLinkBase(string? deepLinkAddress)
    {
        if (string.IsNullOrWhiteSpace(deepLinkAddress))
        {
            return "https://octocon.app/deep";
        }

        return deepLinkAddress.Trim().TrimEnd('/');
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

    protected async Task<IActionResult?> IssueChallengeIfRegisteredAsync(
        string providerKey,
        string operationId,
        Dictionary<string, string>? additionalParameters = null)
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

        if (additionalParameters?.Count > 0)
        {
            foreach (var (key, value) in additionalParameters)
            {
                props.Items[key] = value;
            }
        }

        Response.Headers["X-Interfold-OperationId"] = operationId;
        return Challenge(props, challengeScheme);
    }

    protected Dictionary<string, string>? GetChallengeParameters(string providerKey)
    {
        var authConfig = AuthOptions.CurrentValue;
        return providerKey switch
        {
            "discord" => authConfig.DiscordParameters,
            "google" => authConfig.GoogleParameters,
            "apple" => authConfig.AppleParameters,
            _ => null
        };
    }
}
