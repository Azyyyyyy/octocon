namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// API endpoint and URL configuration for frontend clients and deep linking.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class ApiConfiguration
{
    public const string SectionName = "Octocon:Api";

    /// <summary>
    /// Production frontend address.
    /// Env: OCTOCON_FRONTEND
    /// </summary>
    public string? FrontendAddress { get; set; }

    /// <summary>
    /// Beta/staging frontend address.
    /// Env: OCTOCON_BETA_FRONTEND
    /// </summary>
    public string? BetaFrontendAddress { get; set; }

    /// <summary>
    /// Deep link protocol address (app URI scheme).
    /// Example: 'octocon://app'
    /// Env: OCTOCON_DEEPLINK_ADDRESS
    /// </summary>
    public string? DeepLinkAddress { get; set; }
}
