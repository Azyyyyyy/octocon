using System.Text.RegularExpressions;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Services;

public interface IAvatarStorage
{
    Task<string> SaveSystemAvatarAsync(string systemId, Stream stream, CancellationToken cancellationToken = default);
    Task<string> SaveAlterAvatarAsync(string systemId, int alterId, Stream stream, CancellationToken cancellationToken = default);
    Task<bool> DeleteByUrlAsync(string? avatarUrl, CancellationToken cancellationToken = default);
}

public sealed class LocalAvatarStorage : IAvatarStorage
{
    private static readonly Regex SafeSegmentPattern = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    private readonly IOptionsMonitor<StorageConfiguration> _storageOptions;
    private readonly string _webRootFallback;
    private readonly string _publicBaseFallback;

    // Read CurrentValue per-access so appsettings.json changes take effect without restart.
    private string StorageRoot => _storageOptions.CurrentValue.AvatarStorageRoot ?? _webRootFallback;
    private string PublicBase  => _storageOptions.CurrentValue.AvatarPublicBase  ?? _publicBaseFallback;

    public LocalAvatarStorage(IWebHostEnvironment environment, IOptionsMonitor<StorageConfiguration> storageOptions)
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        }

        _storageOptions     = storageOptions;
        _webRootFallback    = Path.Combine(webRoot, "avatars");
        _publicBaseFallback = "/avatars";
    }

    public Task<string> SaveSystemAvatarAsync(string systemId, Stream stream, CancellationToken cancellationToken = default)
        => SaveAsync(systemId, "self", stream, cancellationToken);

    public Task<string> SaveAlterAvatarAsync(string systemId, int alterId, Stream stream, CancellationToken cancellationToken = default)
        => SaveAsync(systemId, alterId.ToString(), stream, cancellationToken);

    public Task<bool> DeleteByUrlAsync(string? avatarUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return Task.FromResult(false);

        var basePath = PublicBase.TrimEnd('/');
        var storageRoot = Path.GetFullPath(StorageRoot);
        var storageRootWithSep = storageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? storageRoot
            : storageRoot + Path.DirectorySeparatorChar;

        var urlPath = avatarUrl;
        if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out var absoluteUri))
            urlPath = absoluteUri.AbsolutePath;

        if (string.IsNullOrWhiteSpace(urlPath))
            return Task.FromResult(false);

        if (!urlPath.StartsWith('/'))
            urlPath = "/" + urlPath;

        if (!urlPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        var relativePath = urlPath[basePath.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullFilePath = Path.GetFullPath(Path.Combine(storageRoot, relativePath));

        // Refuse to delete anything outside avatar storage root.
        if (!fullFilePath.StartsWith(storageRootWithSep, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        if (!File.Exists(fullFilePath))
            return Task.FromResult(false);

        File.Delete(fullFilePath);
        TryDeleteEmptyParentDirectories(storageRoot, Path.GetDirectoryName(fullFilePath));
        return Task.FromResult(true);
    }

    private async Task<string> SaveAsync(string systemId, string targetId, Stream stream, CancellationToken cancellationToken)
    {
        var safeSystemId = SafeSegmentPattern.Replace(systemId, "_");
        var safeTargetId = SafeSegmentPattern.Replace(targetId, "_");

        if (stream.CanSeek)
            stream.Position = 0;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();
        if (content.Length == 0)
            throw new InvalidOperationException("Uploaded file has no readable content.");

        var extension = DetectExtension(content);
        var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{extension}";

        var directoryPath = Path.Combine(StorageRoot, safeSystemId, safeTargetId);
        Directory.CreateDirectory(directoryPath);

        var filePath = Path.Combine(directoryPath, fileName);
        await File.WriteAllBytesAsync(filePath, content, cancellationToken);

        var basePath = PublicBase.TrimEnd('/');
        return $"{basePath}/{safeSystemId}/{safeTargetId}/{fileName}";
    }

    private static string DetectExtension(byte[] data) => data switch
    {
        [0xFF, 0xD8, ..] => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0x47, 0x49, 0x46, ..] => ".gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        _ => ".bin"
    };

    private static void TryDeleteEmptyParentDirectories(string storageRoot, string? directoryPath)
    {
        var rootFull = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar);
        var current = directoryPath;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var currentFull = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(currentFull, rootFull, StringComparison.OrdinalIgnoreCase))
                return;

            if (!Directory.Exists(currentFull))
                return;

            if (Directory.EnumerateFileSystemEntries(currentFull).Any())
                return;

            Directory.Delete(currentFull);
            current = Path.GetDirectoryName(currentFull);
        }
    }
}
