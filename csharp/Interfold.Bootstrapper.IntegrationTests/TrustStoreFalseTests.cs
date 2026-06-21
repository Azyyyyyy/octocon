using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Confirms the <c>trustStoreInstall=false</c> branch of <c>CertificatePhase</c> really does
/// leave the system trust store untouched. The default test fixture config sets the flag to
/// false; this test runs <c>publish</c> with that config and asserts the Debian anchor
/// directory (<c>/usr/local/share/ca-certificates/</c>) has no <c>interfold-root-ca*</c>
/// symlinks afterwards.
/// </summary>
/// <remarks>
/// The complementary positive case (<c>trustStoreInstall=true</c> → root CA appears under
/// <c>/etc/ssl/certs/</c>) is already covered by
/// <see cref="UbuntuBootstrapTests.RootCaInstalledInDebianTrustStore"/>.
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class TrustStoreFalseTests(UbuntuDinDFixture dinD)
{
    private static string TestConfigJsonPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.json");

    [After(Test)]
    public async Task DumpOnFailure(TestContext ctx)
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }
        // publish-only — no compose up, but still safe to call.
        await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
    }

    [Test]
    public async Task TrustStoreInstallFalseLeavesSystemStoreClean()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(TrustStoreInstallFalseLeavesSystemStoreClean), TestConfigJsonPath);

        var result = await dinD.RunBootstrapperAsync(nameof(TrustStoreInstallFalseLeavesSystemStoreClean),
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);

        // /usr/local/share/ca-certificates is the Debian anchor dir; update-ca-certificates would
        // also copy the anchor PEM into /etc/ssl/certs as a symlink. Both paths must be free of
        // interfold-* entries when the install branch is skipped.
        var anchorList = await dinD.ExecAsync(
            ["sh", "-c", "ls /usr/local/share/ca-certificates/ 2>/dev/null | grep -i interfold || true"]);
        await Assert.That(anchorList.Stdout.Trim()).IsEmpty()
            .Because($"trustStoreInstall=false should not write to the anchor directory; saw: {anchorList.Stdout}");

        var sslCertsList = await dinD.ExecAsync(
            ["sh", "-c", "ls /etc/ssl/certs/ 2>/dev/null | grep -i interfold || true"]);
        await Assert.That(sslCertsList.Stdout.Trim()).IsEmpty()
            .Because($"trustStoreInstall=false should not surface in /etc/ssl/certs; saw: {sslCertsList.Stdout}");

        // Sanity: the generated cert files themselves should still exist under the scratch dir —
        // it's only the SYSTEM trust install that should be skipped.
        var leafExists = await dinD.ExecAsync(["test", "-f", $"{scratch.OutputDir}/certs/leaf.crt"]);
        await Assert.That(leafExists.ExitCode).IsEqualTo(0L)
            .Because("the leaf cert artifact must still be produced even when trust-store install is off");
    }
}
