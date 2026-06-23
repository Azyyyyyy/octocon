using System.Reflection;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Materialises the support files embedded in this assembly (Scylla rackdc properties, the nginx
/// envsubst template, ensure-host-aio.sh, etc.) next to the bootstrapper binary on first run.
///
/// The csproj registers each support file as an <c>&lt;EmbeddedResource&gt;</c> with a
/// <c>LogicalName</c> of <c>support/&lt;relative-on-disk-path&gt;</c>. This helper enumerates every
/// resource under the <c>support/</c> prefix at runtime, so adding a new file is a pure csproj
/// change with no C# manifest to keep in sync.
///
/// Operator override: any file that already exists on disk is preserved verbatim — a power user
/// can drop a customised rackdc properties file or nginx template next to the binary and reruns
/// will leave it alone.
/// </summary>
internal static class EmbeddedSupportFiles
{
    private const string ResourcePrefix = "support/";

    /// <summary>
    /// Extracts every embedded support resource under <paramref name="baseDir"/> unless the target
    /// file already exists. Logs a single summary line via <paramref name="logger"/> when at least
    /// one file is newly extracted; stays silent otherwise.
    /// </summary>
    /// <param name="baseDir">
    /// Root directory the resource paths are resolved against — in production this is
    /// <see cref="AppContext.BaseDirectory"/>, in tests a fresh temp directory.
    /// </param>
    /// <param name="logger">Phase logger used for the extraction summary.</param>
    public static void EnsureExtracted(string baseDir, PhaseLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);
        ArgumentNullException.ThrowIfNull(logger);

        var asm = typeof(EmbeddedSupportFiles).Assembly;
        // Normalise once so the path-traversal guard below uses a stable, canonical prefix.
        var canonicalBase = Path.GetFullPath(baseDir);
        var extracted = 0;

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // Resource LogicalNames use forward slashes; translate to native separators so the
            // path joins correctly on every platform the test suite might run on.
            var relativePath = resourceName[ResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(canonicalBase, relativePath));

            // Defensive: LogicalNames are author-controlled, but a stray `..` in a future entry
            // would silently let us write outside baseDir. Cheap to assert.
            if (!IsUnderBase(targetPath, canonicalBase))
            {
                throw new InvalidOperationException(
                    $"Embedded support resource '{resourceName}' resolves to '{targetPath}', " +
                    $"which is outside baseDir '{canonicalBase}'.");
            }

            if (File.Exists(targetPath))
            {
                continue;
            }

            if (ExtractResource(asm, resourceName, targetPath))
            {
                extracted++;
            }
        }

        if (extracted > 0)
        {
            logger.Info(
                $"    extracted {extracted} support file(s) under {canonicalBase} (existing files preserved)");
        }
    }

    private static bool IsUnderBase(string candidate, string canonicalBase)
    {
        // Append the separator to baseDir so /opt/interfold-bootstrap-evil does not pass as a
        // sibling of /opt/interfold-bootstrap.
        var withSep = canonicalBase.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalBase
            : canonicalBase + Path.DirectorySeparatorChar;
        return candidate.StartsWith(withSep, StringComparison.Ordinal) || candidate == canonicalBase;
    }

    /// <summary>
    /// Streams <paramref name="resourceName"/> from the assembly to a sibling temp file then
    /// atomically renames it to <paramref name="targetPath"/>. Returns <c>true</c> on success and
    /// <c>false</c> if a concurrent writer (parallel test invocation, simultaneous operator run,
    /// or operator drop-in between our File.Exists check and the rename) got there first — in that
    /// case the on-disk file is either an identical resource copy or operator-intent, so we leave
    /// it alone and report "no new extraction" rather than failing the run.
    /// </summary>
    private static bool ExtractResource(Assembly asm, string resourceName, string targetPath)
    {
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Manifest resource '{resourceName}' enumerated but GetManifestResourceStream returned null.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // Per-process temp suffix so two bootstrappers running against the same baseDir never
        // clobber each other's intermediate file. The atomic rename below is what actually
        // serialises the writers; this suffix just avoids self-inflicted FileStream collisions.
        var tempPath = $"{targetPath}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fs);
            }

            try
            {
                File.Move(tempPath, targetPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                // Lost the rename race against another extractor; clean up our temp file and
                // return false so the caller does not double-count this as a new extraction.
                File.Delete(tempPath);
                return false;
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* swallowed */ }
            throw;
        }

        MaybeSetExecutableBit(targetPath);
        return true;
    }

    private static void MaybeSetExecutableBit(string targetPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!targetPath.EndsWith(".sh", StringComparison.Ordinal))
        {
            return;
        }

        var current = File.GetUnixFileMode(targetPath);
        File.SetUnixFileMode(targetPath,
            current
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
    }
}
