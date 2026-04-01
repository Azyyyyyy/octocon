namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// Local file storage configuration for avatars and other static assets.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class StorageConfiguration
{
    public const string SectionName = "Octocon:Storage";

    /// <summary>
    /// Local filesystem root directory for storing uploaded avatars.
    /// Env: OCTOCON_AVATAR_STORAGE_ROOT
    /// </summary>
    public string? AvatarStorageRoot { get; set; }

    /// <summary>
    /// Public base URL for accessing stored avatars (e.g., 'https://cdn.example.com/avatars/').
    /// Used to construct URL responses for avatar retrieval endpoints.
    /// Env: OCTOCON_AVATAR_PUBLIC_BASE
    /// </summary>
    public string? AvatarPublicBase { get; set; }
}
