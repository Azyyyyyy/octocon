using System.Text.Json;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Services;

/// <summary>
/// Handles Apple OAuth2 backend token exchange and identity extraction.
/// </summary>
public sealed class AppleOAuthService
{
    private const string TokenEndpoint = "https://appleid.apple.com/auth/token";

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public AppleOAuthService(HttpClient httpClient, IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        _httpClient = httpClient;
        _authOptions = authOptions;
    }

    /// <summary>
    /// Exchanges an Apple authorization code and returns the stable Apple user identifier (sub).
    /// Returns null if configuration is incomplete or exchange fails.
    /// </summary>
    public async Task<string?> ExchangeCodeForAppleIdAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var authConfig = _authOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(authConfig.AppleOAuthClientId) ||
            string.IsNullOrWhiteSpace(authConfig.AppleOAuthClientSecret))
        {
            return null;
        }

        try
        {
            var tokenRequest = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", redirectUri },
                { "client_id", authConfig.AppleOAuthClientId },
                { "client_secret", authConfig.AppleOAuthClientSecret }
            };

            using var content = new FormUrlEncodedContent(tokenRequest);
            using var tokenResponse = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            using var tokenDoc = JsonDocument.Parse(tokenJson);
            if (!tokenDoc.RootElement.TryGetProperty("id_token", out var idTokenProp))
            {
                return null;
            }

            return ExtractSubFromJwt(idTokenProp.GetString());
        }
        catch
        {
            return null;
        }
    }

    public string? ExtractSubFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            using var payloadDoc = JsonDocument.Parse(payloadBytes);
            if (payloadDoc.RootElement.TryGetProperty("sub", out var subProp))
            {
                return subProp.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var normalized = input
            .Replace('-', '+')
            .Replace('_', '/');

        var padding = 4 - (normalized.Length % 4);
        if (padding is > 0 and < 4)
        {
            normalized = normalized + new string('=', padding);
        }

        return Convert.FromBase64String(normalized);
    }
}
