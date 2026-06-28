using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Shared helpers for the unit-test project.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// True when the test process is running inside a CI environment.
    ///
    /// Uses the de-facto standard <c>CI</c> env var (set to <c>true</c> by GitHub Actions,
    /// GitLab, CircleCI, Travis, Buildkite, etc). Tests that prefer SKIP over FAIL when a
    /// prerequisite artifact is missing should still FAIL in CI — a silent skip there masks
    /// a real workflow regression (e.g. a publish/stage step that broke without anyone
    /// noticing because the test report still says "0 failed").
    /// </summary>
    public static bool IsRunningInCi =>
        string.Equals(
            Environment.GetEnvironmentVariable("CI"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the path to the bootstrapper assembly (so <c>dotnet &lt;path&gt;</c> can launch it
    /// without needing to publish first). If the build artifact isn't on disk yet, throws a
    /// <see cref="SkipTestException"/> so the test is reported as skipped rather than failed —
    /// this happens when the unit project is run before the bootstrapper has been compiled.
    /// </summary>
    public static string BootstrapperBinaryOrSkip()
    {
        var path = LocateBootstrapperAssembly();
        if (path is null || !File.Exists(path))
        {
            throw new SkipTestException(
                "Bootstrapper assembly not found on disk; build Interfold.Bootstrapper first " +
                "or run via `dotnet test` from the solution root.");
        }
        return path;
    }

    /// <summary>
    /// Walks up from the test assembly's location to <c>csharp/Interfold.Bootstrapper/bin/</c>
    /// looking for a Debug or Release artifact. We try both because contributors may have only
    /// built one configuration locally and CI builds Release.
    /// </summary>
    public static string? LocateBootstrapperAssembly()
    {
        // AppContext.BaseDirectory is .../Interfold.Bootstrapper.UnitTests/bin/<config>/net10.0/
        // Climb up to the csharp/ root, then drop into Interfold.Bootstrapper/bin.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "csharp")
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        // Prefer the same configuration the tests were built with, fall back to the other.
        string[] candidates =
        [
            Path.Combine(dir.FullName, "Interfold.Bootstrapper", "bin", "Debug", "net10.0", "interfold-bootstrap.dll"),
            Path.Combine(dir.FullName, "Interfold.Bootstrapper", "bin", "Release", "net10.0", "interfold-bootstrap.dll"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
