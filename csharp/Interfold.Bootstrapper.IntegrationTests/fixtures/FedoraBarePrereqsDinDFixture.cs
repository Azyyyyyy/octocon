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
}
