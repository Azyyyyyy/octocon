using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Negative-path coverage: when the bootstrapper runs on a distro family it does not support,
/// it must exit non-zero with a clear "unsupported distro" message and must not have made
/// partial filesystem changes under its output directory. The fixture is a glibc Debian-slim base
/// with a doctored <c>/etc/os-release</c> so the binary actually execs before being rejected.
/// </summary>
[RequiresDocker]
[ClassDataSource<UnsupportedDistroDinDFixture>(Shared = SharedType.PerTestSession)]
public class UnsupportedOsTests(UnsupportedDistroDinDFixture dinD)
{
    private static string TestConfigJsonPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.json");

    [After(Test)]
    public async Task DumpOnFailure(TestContext ctx)
    {
        if (ctx.Execution.Result?.State != TestState.Failed) return;
        await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
    }

    [Test]
    public async Task RefusesToRunOnUnsupportedDistro()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RefusesToRunOnUnsupportedDistro), TestConfigJsonPath);

        var result = await dinD.RunBootstrapperAsync(nameof(RefusesToRunOnUnsupportedDistro),
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0L)
            .Because("bootstrap must exit non-zero on an unsupported distro");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("Unsupported Linux distribution").Or.Contains("unsupported-distro")
            .Because("error message should explicitly identify the unsupported distro");

        // Filesystem state must be untouched - secrets/certs/compose should not exist.
        var any = await dinD.ExecAsync(
            ["sh", "-c", $"ls {scratch.OutputDir}/secrets {scratch.OutputDir}/certs {scratch.OutputDir}/docker-compose.yaml 2>/dev/null | wc -l"]);
        await Assert.That(any.Stdout.Trim()).IsEqualTo("0")
            .Because("a refused run must not leave partial artifacts under the output dir");
    }
}
