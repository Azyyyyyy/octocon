using System.Text.Json;
using Microsoft.Extensions.Options;
using Interfold.Infrastructure.Configuration;

namespace Interfold.Api.Services;

/// <summary>
/// Handles Discord OAuth2 backend token exchange and user info retrieval.
/// </summary>
public sealed class DiscordOAuthService
{
    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";
    private const string UserInfoEndpoint = "https://discord.com/api/users/@me";

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public DiscordOAuthService(HttpClient httpClient, IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        _httpClient = httpClient;
        _authOptions = authOptions;
    }

    /// <summary>
    /// Exchange authorization code for the Discord user ID via Discord's OAuth2 flow.
    /// Returns null if configuration is incomplete or exchange fails.
    /// </summary>
    public async Task<string?> ExchangeCodeForDiscordIdAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var authConfig = _authOptions.CurrentValue;

        if (string.IsNullOrWhiteSpace(authConfig.DiscordOAuthClientId) ||
            string.IsNullOrWhiteSpace(authConfig.DiscordOAuthClientSecret))
        {
            return null;
        }

        try
        {
            // Step 1: Exchange authorization code for access token.
            // Discord requires application/x-www-form-urlencoded with HTTP Basic auth.
            var tokenRequest = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", redirectUri }
            };

            using var content = new FormUrlEncodedContent(tokenRequest);
            var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = content };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{authConfig.DiscordOAuthClientId}:{authConfig.DiscordOAuthClientSecret}")));

            using var tokenResponse = await _httpClient.SendAsync(request, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            using var tokenDoc = JsonDocument.Parse(tokenJson);

            if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenProp))
            {
                return null;
            }

            var accessToken = accessTokenProp.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            // Step 2: Fetch the user's Discord ID from /users/@me.
            var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            userInfoRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var userInfoResponse = await _httpClient.SendAsync(userInfoRequest, cancellationToken);

            if (!userInfoResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var userInfoJson = await userInfoResponse.Content.ReadAsStringAsync(cancellationToken);
            using var userInfoDoc = JsonDocument.Parse(userInfoJson);

            if (userInfoDoc.RootElement.TryGetProperty("id", out var idProp))
            {
                return idProp.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
