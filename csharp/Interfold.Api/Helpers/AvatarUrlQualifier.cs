using Interfold.Contracts.Enums;

namespace Interfold.Api.Helpers;

internal static class AvatarUrlQualifier
{
    /// <summary>
    /// Returns <paramref name="url"/> with the server origin prepended when the stored
    /// value is a relative path. Already-absolute URLs are returned unchanged.
    /// </summary>
    /// <remarks>
    /// Avatar paths now route through <see cref="QualifyAvatar(string?, AvatarSource?, string, HostString)"/>
    /// (or the origin overload) so they can use the persisted <c>avatar_source</c> as the
    /// authoritative discriminator. This raw helper is retained for non-avatar callers
    /// (e.g. internal utilities) that don't have a source flag to inspect.
    /// </remarks>
    internal static string? Qualify(string? url, string scheme, HostString host)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        var origin = $"{scheme}://{host}";
        return url.StartsWith('/') ? $"{origin}{url}" : $"{origin}/{url}";
    }

    /// <summary>
    /// Overload for callers that already hold a pre-built origin string
    /// (e.g. <c>"https://api.example.com"</c>).
    /// </summary>
    internal static string? Qualify(string? url, string? origin)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (string.IsNullOrWhiteSpace(origin))
            return url;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        return url.StartsWith('/') ? $"{origin}{url}" : $"{origin}/{url}";
    }

    /// <summary>
    /// Source-aware avatar qualification. The persisted <see cref="AvatarSource"/> is the
    /// single source of truth: <see cref="AvatarSource.Local"/> URLs get the server origin
    /// prepended, <see cref="AvatarSource.External"/> URLs are returned verbatim, and a
    /// null source (no avatar set) is passed through unchanged.
    /// </summary>
    internal static string? QualifyAvatar(string? url, AvatarSource? source, string scheme, HostString host)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        return source == AvatarSource.Local
            ? Qualify(url, scheme, host)
            : url;
    }

    /// <summary>
    /// Origin-string overload of <see cref="QualifyAvatar(string?, AvatarSource?, string, HostString)"/>.
    /// </summary>
    internal static string? QualifyAvatar(string? url, AvatarSource? source, string? origin)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        return source == AvatarSource.Local
            ? Qualify(url, origin)
            : url;
    }
}
