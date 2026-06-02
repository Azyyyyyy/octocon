using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Operations;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Api.Controllers.Base;

namespace Interfold.Api.Controllers;

[AllowAnonymous]
[Route("auth/link")]
public sealed class AuthLinkController : OAuthControllerBase
{
    private const string LinkTokenCookieName = "octocon_link_token";
    private const string RedirectUriCookieName = "octocon_link_redirect_uri";

    private readonly IAccountRepository _accounts;
    private readonly IClusterEventBus _eventBus;

    public AuthLinkController(
        IAccountRepository accounts,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IOptionsMonitor<ApiConfiguration> apiOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        IClusterEventBus eventBus)
        : base(authOptions, apiOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _accounts = accounts;
        _eventBus = eventBus;
    }

    protected override string CallbackRoutePrefix => "auth/link";

    [HttpGet("{provider}")]
    public async Task<IActionResult> Begin([FromRoute] string provider)
    {
        if (!IsSupportedProvider(provider))
            return UnsupportedProviderResponse(provider);

        StoreQueryCookie(LinkTokenCookieName, "link_token");
        StoreRedirectUriCookie(RedirectUriCookieName);

        var providerKey = provider.ToLowerInvariant();
        var challenge = await IssueChallengeIfRegisteredAsync(providerKey, OperationIds.QueryAuthLinkRequest);

        if (challenge is not null)
            return challenge;

        Response.Headers["X-Interfold-OperationId"] = OperationIds.QueryAuthLinkRequest;
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
            return UnsupportedProviderResponse(provider);

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

        var redirectUri = Request.Cookies[RedirectUriCookieName];
        Response.Cookies.Delete(RedirectUriCookieName);

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

        Response.Headers["X-Interfold-OperationId"] = OperationIds.AuthLinkCallback;

        return result switch
        {
            AccountLinkResult.Success => await RedirectWithSocketEventAsync(systemId, providerKey, identity, redirectUri),
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

    private async Task<IActionResult> RedirectWithSocketEventAsync(string systemId, string providerKey, string identity, string? redirectUri)
    {
        await _eventBus.PublishAsync(
            new SettingsAccountLinkedEvent(systemId, providerKey, identity),
            HttpContext.RequestAborted);

        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            return Redirect(redirectUri);
        }

        var apiConfig = ApiOptions.CurrentValue;
        var deepLinkBase = NormalizeDeepLinkBase(apiConfig.DeepLinkAddress);
        var redirectBase = deepLinkBase.EndsWith("/deep", StringComparison.OrdinalIgnoreCase)
            ? deepLinkBase
            : $"{deepLinkBase}/deep";

        return Redirect($"{redirectBase}/link_success/{providerKey}");
    }
}
