namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// DinD fixture rooted on a stripped Fedora 40 image with neither <c>docker</c> nor
/// <c>openssl</c> pre-installed. Used exclusively by <c>PrereqsPhaseTests</c> to exercise the
/// dnf install path inside <c>PrerequisitesPhase</c> (the RedHat-family counterpart of the
/// Ubuntu bare fixture).
/// </summary>
public sealed class FedoraBarePrereqsDinDFixture : DinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.fedora-bare-dind";

    // Same rationale as UbuntuBarePrereqsDinDFixture: no dockerd inside, so the parent's
    // PreloadImages flow has nothing to load and nothing to pull.
    protected override bool PreloadImages => false;

    // Mirrors UbuntuBarePrereqsDinDFixture.InitializeAsync: assert the bare Fedora image
    // ships without docker exactly once at fixture build time instead of relying on a
    // first-test-wins precondition guard inside InstallsDockerOnFedoraWhenAbsent. See that
    // fixture's comment for the full rationale.
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync().ConfigureAwait(false);
        var prePath = await ExecAsync(["sh", "-c", "command -v docker || true"]).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prePath.Stdout))
        {
            throw new InvalidOperationException(
                $"{nameof(FedoraBarePrereqsDinDFixture)} expects a bare Fedora image without docker " +
                $"pre-installed, but `command -v docker` returned '{prePath.Stdout.Trim()}'. " +
                "Inspect Dockerfile.fedora-bare-dind — the dnf install path can no longer be exercised.");
        }
    }
}
