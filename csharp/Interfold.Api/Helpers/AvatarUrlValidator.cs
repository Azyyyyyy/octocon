namespace Interfold.Api.Helpers;

/// <summary>
/// Boundary-only check for client-supplied avatar URLs. The server never fetches
/// the URL — it just stores it on <c>avatar_url</c> with <c>avatar_source = External</c> —
/// so this validator only enforces shape (absolute, http/https, length-capped).
/// </summary>
internal static class AvatarUrlValidator
{
    /// <summary>
    /// Maximum stored URL length. Matches conservative defaults for URI columns
    /// across the persistence backends; SP CDN URLs comfortably fit inside it.
    /// </summary>
    internal const int MaxLength = 2048;

    internal const string ErrorInvalid = "avatar_url_invalid";
    internal const string ErrorTooLong = "avatar_url_too_long";

    /// <summary>
    /// Validates and normalises <paramref name="raw"/>. Returns the trimmed URL on success.
    /// </summary>
    internal static bool TryNormalize(string? raw, out string url, out string errorCode)
    {
        url = string.Empty;
        errorCode = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            errorCode = ErrorInvalid;
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength)
        {
            errorCode = ErrorTooLong;
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            errorCode = ErrorInvalid;
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            errorCode = ErrorInvalid;
            return false;
        }

        url = uri.ToString();
        if (url.Length > MaxLength)
        {
            errorCode = ErrorTooLong;
            url = string.Empty;
            return false;
        }

        return true;
    }
}
