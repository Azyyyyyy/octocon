using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Asserts the argv shape <see cref="BackupPhase"/> hands to docker compose for both the
/// pg_dump and nodetool snapshot pipelines. These tests don't exec anything — they just
/// pin the argv contract so a future refactor that swaps argument order or adds a flag
/// catches the change at unit speed rather than in the DinD integration suite.
/// </summary>
public sealed class BackupCommandBuildingTests
{
    [Test]
    public async Task PostgresDumpArgsUseAdminUserAndDatabase()
    {
        var args = BackupPhase.BuildPostgresDumpArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            adminUser: "interfold_admin",
            database: "interfold");

        // Order is load-bearing: `compose` must be the first arg so docker dispatches to the
        // compose plugin; `-f` must come before the file path; `exec -T msg-db` must precede
        // pg_dump and its flags. Asserting the full sequence catches any silent re-ordering.
        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T",
            "--env", "PGPASSWORD",
            "msg-db",
            "pg_dump",
            "-U", "interfold_admin",
            "-d", "interfold",
            "-Fc",
        });
    }

    [Test]
    public async Task PostgresDumpArgsDoNotContainPlaintextPassword()
    {
        // The password lands on the exec'd process via the PGPASSWORD env var (set elsewhere)
        // rather than the argv. Argv ends up in `ps` output and audit logs; passwords must
        // never appear there. We just assert no recognisable secret-shaped string.
        var args = BackupPhase.BuildPostgresDumpArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            adminUser: "interfold_admin",
            database: "interfold");

        await Assert.That(args).DoesNotContain("PGPASSWORD=anything");
        // The literal "PGPASSWORD" appears as the env-var name (no `=value` suffix); that's
        // safe — only the value side leaks to ps.
        foreach (var arg in args)
        {
            await Assert.That(arg).DoesNotContain("password");
            await Assert.That(arg).DoesNotContain("secret");
        }
    }

    [Test]
    public async Task ScyllaSnapshotArgsTargetCorrectService()
    {
        var (snap, tar, clear) = BackupPhase.BuildScyllaSnapshotArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            service: "scylla",
            dataPath: "/var/lib/scylla",
            tag: "interfold-backup-20260301-120000");

        await Assert.That(snap).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T", "scylla",
            "nodetool", "snapshot", "-t", "interfold-backup-20260301-120000",
        });

        // tar argv must -C into the data dir + emit to stdout. The leading "." (vs "./") is
        // intentional — both work with GNU tar; "." matches what the spec in the plan documents.
        await Assert.That(tar).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T", "scylla",
            "tar", "-C", "/var/lib/scylla", "-czf", "-", ".",
        });

        await Assert.That(clear).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T", "scylla",
            "nodetool", "clearsnapshot", "-t", "interfold-backup-20260301-120000",
        });
    }

    [Test]
    public async Task ResolveScyllaSeedSingleModeUsesScyllaService()
    {
        // single-mode AppHost wires up the bare "scylla" service name; matches the AppHost
        // resource graph in InterfoldAppHost.Configure.
        var config = new BootstrapConfig { DatabaseMode = "single" };
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        await Assert.That(service).IsEqualTo("scylla");
        await Assert.That(dataPath).IsEqualTo("/var/lib/scylla");
    }

    [Test]
    public async Task ResolveScyllaSeedMultiModeUsesNamSeed()
    {
        // multi-mode publishes 7 regional services; the seed (NAM) is the only one we
        // snapshot. Multi-DC operators that want all seven captured are documented as
        // out-of-scope for the bootstrapper itself.
        var config = new BootstrapConfig { DatabaseMode = "multi" };
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        await Assert.That(service).IsEqualTo("scylla-nam");
        await Assert.That(dataPath).IsEqualTo("/var/lib/scylla");
    }

    [Test]
    public async Task ResolveScyllaSeedCassandraModeUsesCassandraService()
    {
        // cassandra-mode replaces Scylla entirely with a single Cassandra 5 node; the data
        // directory inside the official cassandra image is /var/lib/cassandra (not /var/lib/scylla).
        var config = new BootstrapConfig { DatabaseMode = "cassandra" };
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        await Assert.That(service).IsEqualTo("cassandra");
        await Assert.That(dataPath).IsEqualTo("/var/lib/cassandra");
    }

    [Test]
    public async Task ResolveBackupRootPrefersCliOverride()
    {
        // Operator escape hatch wins over both config and the default. Path is normalised to
        // an absolute one because BackupPhase later wraps it in Directory.CreateDirectory +
        // EnumerateFiles, and both behave erratically with relative paths under a systemd
        // unit's unpredictable CWD.
        var options = new BootstrapOptions(
            Command: BootstrapCommand.Backup,
            ConfigPath: null,
            OutputDir: Path.GetFullPath("./deploy"),
            SkipPrereqs: false,
            RotateSecrets: false,
            RotateCerts: false,
            NonInteractive: false,
            FaultInject: null,
            PrintPhaseStatus: false,
            BackupDirOverride: "/srv/backups");

        var config = new BootstrapConfig { Backup = { Directory = "/var/never-seen" } };
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        // Path.GetFullPath normalises to the platform's separator style; just check it ends
        // with the expected suffix to keep this portable across Windows / Linux runners.
        await Assert.That(resolved.Replace('\\', '/'))
            .Contains("/srv/backups");
    }

    [Test]
    public async Task ResolveBackupRootFallsBackToConfig()
    {
        var options = new BootstrapOptions(
            Command: BootstrapCommand.Backup,
            ConfigPath: null,
            OutputDir: Path.GetFullPath("./deploy"),
            SkipPrereqs: false,
            RotateSecrets: false,
            RotateCerts: false,
            NonInteractive: false,
            FaultInject: null,
            PrintPhaseStatus: false);

        var config = new BootstrapConfig { Backup = { Directory = "/var/backups/interfold" } };
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        await Assert.That(resolved.Replace('\\', '/'))
            .Contains("/var/backups/interfold");
    }

    [Test]
    public async Task ResolveBackupRootDefaultsToOutputDirSubfolder()
    {
        // Neither CLI nor config supplied → fall back to {outputDir}/backups. This is the
        // default path that 99% of operators will hit; the assertion confirms the join is
        // exactly "backups" (no typos, no leading slash).
        var outputDir = Path.GetFullPath("./deploy");
        var options = new BootstrapOptions(
            Command: BootstrapCommand.Backup,
            ConfigPath: null,
            OutputDir: outputDir,
            SkipPrereqs: false,
            RotateSecrets: false,
            RotateCerts: false,
            NonInteractive: false,
            FaultInject: null,
            PrintPhaseStatus: false);

        var config = new BootstrapConfig();
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        await Assert.That(resolved).IsEqualTo(Path.Combine(outputDir, "backups"));
    }

    [Test]
    public async Task BuildArchiveFileNameProducesExpectedExtensions()
    {
        // Postgres uses pg_dump's custom binary format (.dump) restorable with pg_restore;
        // Scylla uses gzipped tar of the nodetool snapshot tree. The extensions are part of
        // the documented backup layout that operators rely on for ad-hoc tooling — pinning
        // them here so a refactor can't quietly switch to .pgdump or .tgz.
        await Assert.That(BackupPhase.BuildArchiveFileName("postgres", "20260301-120000"))
            .IsEqualTo("20260301-120000.dump");
        await Assert.That(BackupPhase.BuildArchiveFileName("scylla", "20260301-120000"))
            .IsEqualTo("20260301-120000.tar.gz");
    }

    [Test]
    public async Task BuildArchiveFileNameRejectsUnknownComponent()
    {
        // Defensive; the orchestrator path validates the component upfront, but the helper
        // should still refuse to silently produce a meaningless filename if it's ever
        // called from a future code path that forgot to validate.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BackupPhase.BuildArchiveFileName("redis", "20260301-120000"));
        await Assert.That(ex.Message).Contains("redis");
        await Assert.That(ex.Message).Contains("postgres");
        await Assert.That(ex.Message).Contains("scylla");
    }
}
