using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>install-service</c> subcommand. The shared Ubuntu DinD
/// fixture deliberately does NOT include systemd (its PID 1 is <c>dockerd-entrypoint.sh</c>,
/// not <c>systemd</c>), so we exercise the unit-file rendering + on-disk write path against
/// an operator-supplied unit dir (<c>--systemd-unit-dir</c>) and skip the daemon-reload /
/// enable steps. For tests that need <c>systemd-analyze</c> coverage we install the
/// <c>systemd</c> apt package on demand — the binary itself works fine outside of a
/// systemd-managed PID 1.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class SystemdInstallTests(UbuntuDinDFixture dinD)
{
    private static string TestConfigJsonPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.json");

    [After(Test)]
    public async Task DumpOnFailure(TestContext ctx)
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }
        await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
    }

    [Test]
    public async Task WritesAllThreeUnitFilesToTargetDir()
    {
        // We don't need a live compose stack for install-service — it only reads the bootstrap
        // config + writes unit files. Skip the full bootstrap to keep the test fast.
        var scratch = await dinD.CreateScratchAsync(nameof(WritesAllThreeUnitFilesToTargetDir), TestConfigJsonPath);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunBootstrapperAsync(nameof(WritesAllThreeUnitFilesToTargetDir),
            ["install-service",
             "--config", scratch.ConfigPath,
             "--output-dir", scratch.OutputDir,
             "--systemd-unit-dir", unitDir,
             "--non-interactive"]);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        // Each of the three units must be present on disk.
        foreach (var name in new[] { "interfold.service", "interfold-backup.service", "interfold-backup.timer" })
        {
            var probe = await dinD.ExecAsync(["test", "-f", $"{unitDir}/{name}"]);
            await Assert.That(probe.ExitCode).IsEqualTo(0L)
                .Because($"expected {unitDir}/{name} to exist after install-service");
        }
    }

    [Test]
    public async Task UnitFilesContainExpectedTokens()
    {
        // Pin the rendered contents: the boot service must use docker compose up -d (not
        // `interfold-bootstrap up`), the backup service must point at the resolved config +
        // outputDir, and the timer must embed the OnCalendar string from the config.
        var scratch = await dinD.CreateScratchAsync(nameof(UnitFilesContainExpectedTokens), TestConfigJsonPath);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunBootstrapperAsync(nameof(UnitFilesContainExpectedTokens),
            ["install-service",
             "--config", scratch.ConfigPath,
             "--output-dir", scratch.OutputDir,
             "--systemd-unit-dir", unitDir,
             "--binary-path", "/opt/bootstrapper/interfold-bootstrap",
             "--non-interactive"]);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        var bootService = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{unitDir}/interfold.service"));
        await Assert.That(bootService).Contains($"ExecStart=/usr/bin/docker compose -f {scratch.OutputDir}/docker-compose.yaml up -d");
        await Assert.That(bootService).Contains("WantedBy=multi-user.target");

        var backupService = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{unitDir}/interfold-backup.service"));
        await Assert.That(backupService).Contains("ExecStart=/opt/bootstrapper/interfold-bootstrap backup");
        await Assert.That(backupService).Contains($"--config {scratch.ConfigPath}");
        await Assert.That(backupService).Contains($"--output-dir {scratch.OutputDir}");

        var timer = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{unitDir}/interfold-backup.timer"));
        // The test fixture's config doesn't override the schedule, so it lands at the default
        // "daily" from BackupSection.Schedule.
        await Assert.That(timer).Contains("OnCalendar=daily");
        await Assert.That(timer).Contains("Persistent=true");
    }

    [Test]
    public async Task SystemdAnalyzeAcceptsRenderedUnits()
    {
        // Install the `systemd` apt package on demand and run `systemd-analyze verify` against
        // every rendered unit. The DinD's PID 1 is dockerd-entrypoint, not systemd — that's
        // fine, systemd-analyze is a static parser that doesn't need a running systemd to run.
        // The package install is cached inside the DinD for the lifetime of the test session,
        // so the first test pays ~10s and the rest are free.
        var install = await dinD.ExecAsync(["sh", "-c",
            "command -v systemd-analyze >/dev/null 2>&1 || (apt-get update >/dev/null 2>&1 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends systemd >/dev/null 2>&1)"]);
        await Assert.That(install.ExitCode).IsEqualTo(0L)
            .Because($"installing systemd inside DinD failed: {install.Stderr}");

        var scratch = await dinD.CreateScratchAsync(nameof(SystemdAnalyzeAcceptsRenderedUnits), TestConfigJsonPath);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunBootstrapperAsync(nameof(SystemdAnalyzeAcceptsRenderedUnits),
            ["install-service",
             "--config", scratch.ConfigPath,
             "--output-dir", scratch.OutputDir,
             "--systemd-unit-dir", unitDir,
             "--binary-path", "/opt/bootstrapper/interfold-bootstrap",
             "--non-interactive"]);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        // The phase itself runs systemd-analyze verify when the binary is on PATH; confirm via
        // the captured logs that it did so and accepted every unit.
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold.service")
            .Because("install-service should report a successful verification line for interfold.service");
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold-backup.service");
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold-backup.timer");
        await Assert.That(result.Stdout + result.Stderr).Contains("calendar 'daily' parses OK")
            .Because("install-service should report systemd-analyze acceptance for the schedule");
    }
}
