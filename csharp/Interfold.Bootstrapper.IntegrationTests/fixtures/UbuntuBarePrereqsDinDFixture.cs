namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// DinD fixture rooted on a stripped Ubuntu 24.04 image with neither <c>docker</c> nor
/// <c>openssl</c> pre-installed. Used exclusively by <c>PrereqsPhaseTests</c> to exercise the
/// apt install path inside <c>PrerequisitesPhase</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DinDFixtureBase.PreloadImages"/> is overridden to <c>false</c> because the
/// container ships without dockerd and therefore can't <c>docker load</c> the API image or
/// pre-pull the Scylla / Timescale images. The trade-off is that any test using this fixture
/// can only validate prerequisites; nothing inside it can actually <c>compose up</c>.
/// </para>
/// <para>
/// We share this with PrereqsPhaseTests via SharedType.PerTestSession (like the other DinD
/// fixtures) but each test inside still runs in its own scratch directory so they don't
/// interfere with each other's installed-package state.
/// </para>
/// </remarks>
public sealed class UbuntuBarePrereqsDinDFixture : DinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.ubuntu-bare-dind";

    // No dockerd to start, no API image to load — skip the parent's PreloadImages flow.
    protected override bool PreloadImages => false;
}
