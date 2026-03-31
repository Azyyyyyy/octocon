using System.Text;
using System.Text.Json;

namespace Interfold.Api.Services;

/// <summary>
/// Handles Google OAuth2 backend token exchange and user info retrieval.
/// </summary>
public sealed class GoogleOAuthService
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";

    private readonly HttpClient _httpClient;
    private readonly ApiSettings _settings;

    public GoogleOAuthService(HttpClient httpClient, ApiSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    /// <summary>
    /// Exchange authorization code for email via Google's OAuth2 flow.
    /// Returns null if configuration is incomplete or exchange fails.
    /// </summary>
    public async Task<string?> ExchangeCodeForEmailAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        // Validate configuration
        if (string.IsNullOrWhiteSpace(_settings.GoogleOAuthClientId) ||
            string.IsNullOrWhiteSpace(_settings.GoogleOAuthClientSecret))
        {
            return null;
        }

        try
        {
            // Step 1: Exchange code for access token
            var tokenRequest = new Dictionary<string, string>
            {
                { "code", code },
                { "client_id", _settings.GoogleOAuthClientId },
                { "client_secret", _settings.GoogleOAuthClientSecret },
                { "grant_type", "authorization_code" },
                { "redirect_uri", redirectUri }
            };

            using var content = new FormUrlEncodedContent(tokenRequest);
            using var tokenResponse = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);

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

            // Step 2: Use access token to fetch user info
            var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            userInfoRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var userInfoResponse = await _httpClient.SendAsync(userInfoRequest, cancellationToken);

            if (!userInfoResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var userInfoJson = await userInfoResponse.Content.ReadAsStringAsync(cancellationToken);
            using var userInfoDoc = JsonDocument.Parse(userInfoJson);

            if (userInfoDoc.RootElement.TryGetProperty("email", out var emailProp))
            {
                return emailProp.GetString();
            }

            return null;
        }
        catch
        {
            // Log the error and return null to allow graceful degradation
            // In production, you'd want to log this properly
            return null;
        }
    }
}
