using TUnit.Core;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Guardrail against accidental binary-size regressions. The bootstrapper is published as a
/// self-contained single-file ELF under <c>csharp/Interfold.Bootstrapper/bin/Release/net10.0/linux-x64/publish</c>;
/// trimming + <c>PublishReadyToRun=false</c> currently keep it well under 75 MiB. If a future
/// change accidentally pulls in a heavy dependency the size will balloon and this test fails
/// before the regression ships.
///
/// The test is skipped (not failed) when the published artifact is absent — that's the normal
/// state during local development; CI publishes before running the test suite.
/// </summary>
public sealed class BinarySizeGuardrailTests
{
    /// <summary>Hard upper bound. Picked from the plan; current size sits well below this.</summary>
    private const long MaxBytes = 75L * 1024 * 1024;

    [Test]
    public async Task PublishedLinuxX64BinaryIsBelowThreshold()
    {
        var path = LocatePublishedBinary();
        if (path is null || !File.Exists(path))
        {
            // No artifact on disk — this is the dev workflow. Skip rather than fail so we don't
            // force every contributor to publish before they can run unit tests.
            throw new SkipTestException(
                "Published linux-x64 binary not found; run `dotnet publish csharp/Interfold.Bootstrapper " +
                "-c Release -r linux-x64 /p:PublishProfile=linux-x64` to generate it.");
        }

        var size = new FileInfo(path).Length;
        await Assert.That(size).IsLessThan(MaxBytes)
            .Because($"binary at {path} is {size / 1024.0 / 1024.0:F1} MiB " +
                     $"(limit {MaxBytes / 1024.0 / 1024.0:F0} MiB)");
    }

    /// <summary>
    /// Walks up to the <c>csharp/</c> directory and checks the published location specified by
    /// the linux-x64 publish profile. The file name is <c>interfold-bootstrap</c> (no extension)
    /// per the assembly name in the csproj.
    /// </summary>
    private static string? LocatePublishedBinary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "csharp")
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        return Path.Combine(
            dir.FullName,
            "Interfold.Bootstrapper",
            "bin",
            "Release",
            "net10.0",
            "linux-x64",
            "publish",
            "interfold-bootstrap");
    }
}
