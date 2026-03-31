using System.Text.RegularExpressions;

namespace Interfold.Api.Services;

public interface IAvatarStorage
{
    Task<string> SaveSystemAvatarAsync(string systemId, IFormFile file, CancellationToken cancellationToken = default);
    Task<string> SaveAlterAvatarAsync(string systemId, int alterId, IFormFile file, CancellationToken cancellationToken = default);
}

public sealed class LocalAvatarStorage : IAvatarStorage
{
    private static readonly Regex SafeSegmentPattern = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    private readonly string _storageRoot;
    private readonly string _publicBase;

    public LocalAvatarStorage(IWebHostEnvironment environment)
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        }

        _storageRoot = Environment.GetEnvironmentVariable("OCTOCON_AVATAR_STORAGE_ROOT")
            ?? Path.Combine(webRoot, "avatars");

        _publicBase = Environment.GetEnvironmentVariable("OCTOCON_AVATAR_PUBLIC_BASE")
            ?? "/avatars";
    }

    public Task<string> SaveSystemAvatarAsync(string systemId, IFormFile file, CancellationToken cancellationToken = default)
        => SaveAsync(systemId, "self", file, cancellationToken);

    public Task<string> SaveAlterAvatarAsync(string systemId, int alterId, IFormFile file, CancellationToken cancellationToken = default)
        => SaveAsync(systemId, alterId.ToString(), file, cancellationToken);

    private async Task<string> SaveAsync(string systemId, string targetId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded file is empty.");
        }

        var safeSystemId = SafeSegmentPattern.Replace(systemId, "_");
        var safeTargetId = SafeSegmentPattern.Replace(targetId, "_");

        var extension = ResolveExtension(file);
        var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{extension}";

        var directoryPath = Path.Combine(_storageRoot, safeSystemId, safeTargetId);
        Directory.CreateDirectory(directoryPath);

        var filePath = Path.Combine(directoryPath, fileName);
        await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var basePath = _publicBase.TrimEnd('/');
        return $"{basePath}/{safeSystemId}/{safeTargetId}/{fileName}";
    }

    private static string ResolveExtension(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName);
        if (!string.IsNullOrWhiteSpace(ext))
        {
            return ext.ToLowerInvariant();
        }

        return file.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".bin"
        };
    }
}
