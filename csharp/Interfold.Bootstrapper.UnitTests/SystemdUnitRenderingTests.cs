using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Asserts the token-substitution semantics of <see cref="SystemdInstallPhase.RenderUnit"/>.
/// We never shell out to systemd-analyze here — that's the integration test's job. These
/// just pin the rendering contract so a future template tweak doesn't silently drop a
/// substitution.
/// </summary>
public sealed class SystemdUnitRenderingTests
{
    /// <summary>Default render input used by every test below; tests override one field at a time.</summary>
    private static SystemdInstallPhase.SystemdRenderInput MakeInput() => new(
        OutputDir: "/srv/interfold/deploy",
        ComposeFile: "/srv/interfold/deploy/docker-compose.yaml",
        ConfigPath: "/srv/interfold/deploy/interfold.bootstrap.json",
        BinaryPath: "/opt/interfold/interfold-bootstrap",
        OnCalendar: "daily");

    [Test]
    public async Task InterfoldServiceContainsExpectedExecStart()
    {
        var rendered = SystemdInstallPhase.RenderUnit("interfold.service", MakeInput());

        // The boot-time autostart is deliberately a direct `docker compose up -d` rather
        // than `interfold-bootstrap up` — the bootstrapper's up path waits up to 5 minutes
        // on /health/ready, which would block the boot critical path. See the plan's
        // "Boot service implementation" decision.
        await Assert.That(rendered).Contains("ExecStart=/usr/bin/docker compose -f /srv/interfold/deploy/docker-compose.yaml up -d");
        await Assert.That(rendered).Contains("ExecStop=/usr/bin/docker compose -f /srv/interfold/deploy/docker-compose.yaml down");
        await Assert.That(rendered).Contains("WorkingDirectory=/srv/interfold/deploy");
        await Assert.That(rendered).Contains("Type=oneshot");
        await Assert.That(rendered).Contains("RemainAfterExit=yes");
        await Assert.That(rendered).Contains("After=docker.service network-online.target");
        await Assert.That(rendered).Contains("Requires=docker.service");
        await Assert.That(rendered).Contains("WantedBy=multi-user.target");
    }

    [Test]
    public async Task InterfoldBackupServiceContainsExpectedExecStart()
    {
        var rendered = SystemdInstallPhase.RenderUnit("interfold-backup.service", MakeInput());

        // The backup service runs the bootstrapper's `backup` subcommand and points at the
        // operator's resolved config + outputDir; the timer fires it on the schedule
        // operators configure via OnCalendar=.
        await Assert.That(rendered).Contains(
            "ExecStart=/opt/interfold/interfold-bootstrap backup " +
            "--config /srv/interfold/deploy/interfold.bootstrap.json " +
            "--output-dir /srv/interfold/deploy " +
            "--component all");
        await Assert.That(rendered).Contains("Type=oneshot");
        // Backup runs after the server is up — operator-visible documentation says the
        // dependency is "soft", so Wants= (not Requires=) is correct.
        await Assert.That(rendered).Contains("Wants=interfold.service");
        await Assert.That(rendered).Contains("After=interfold.service");
    }

    [Test]
    public async Task InterfoldBackupTimerEmbedsOnCalendar()
    {
        var input = MakeInput() with { OnCalendar = "Mon..Fri 03:30" };
        var rendered = SystemdInstallPhase.RenderUnit("interfold-backup.timer", input);

        await Assert.That(rendered).Contains("OnCalendar=Mon..Fri 03:30");
        await Assert.That(rendered).Contains("Persistent=true");
        await Assert.That(rendered).Contains("Unit=interfold-backup.service");
        await Assert.That(rendered).Contains("WantedBy=timers.target");
    }

    [Test]
    public async Task NoUnsubstitutedTokensRemainInAnyUnit()
    {
        // The rendered unit text must never contain `{{` (the template token sentinel).
        // RenderUnit itself throws when it spots a residual token, so this also serves as
        // a smoke test that the renderer's own validation isn't tripping on legitimate text.
        var input = MakeInput();
        foreach (var unitName in SystemdInstallPhase.UnitNames)
        {
            var rendered = SystemdInstallPhase.RenderUnit(unitName, input);
            await Assert.That(rendered).DoesNotContain("{{");
            await Assert.That(rendered).DoesNotContain("}}");
        }
    }

    [Test]
    public async Task RenderUnitThrowsForUnknownUnitName()
    {
        // Defensive: a typo in SystemdInstallPhase.UnitNames would otherwise produce a
        // confusing null-stream error from GetManifestResourceStream. The explicit
        // exception message names the bad template so the maintainer sees the typo.
        var ex = Assert.Throws<InvalidOperationException>(
            () => SystemdInstallPhase.RenderUnit("does-not-exist.service", MakeInput()));
        await Assert.That(ex.Message).Contains("does-not-exist.service");
    }

    [Test]
    public async Task RenderUnitPreservesScheduleSpecialCharacters()
    {
        // systemd accepts comma-separated lists, colons, asterisks, slashes, and ranges in
        // OnCalendar expressions. The renderer must not mangle any of them — the
        // BackupSchedulePattern regex in ConfigPhase.Validate permits them all, so the
        // rendered text must too.
        var input = MakeInput() with { OnCalendar = "*-*-* 02,14:00:00" };
        var rendered = SystemdInstallPhase.RenderUnit("interfold-backup.timer", input);

        await Assert.That(rendered).Contains("OnCalendar=*-*-* 02,14:00:00");
    }

    [Test]
    public async Task RenderUnitWorksForAllRegisteredUnitNames()
    {
        // Smoke: every name listed in UnitNames must have a matching embedded resource so
        // the install loop doesn't crash partway through. Catches a future "added a unit
        // to UnitNames but forgot to add the template" mistake.
        var input = MakeInput();
        foreach (var unitName in SystemdInstallPhase.UnitNames)
        {
            var rendered = SystemdInstallPhase.RenderUnit(unitName, input);
            await Assert.That(rendered.Length).IsGreaterThan(0)
                .Because($"template '{unitName}' must render to a non-empty unit body");
        }
    }
}
