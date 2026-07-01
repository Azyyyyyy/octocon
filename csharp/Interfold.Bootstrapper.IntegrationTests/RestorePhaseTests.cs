using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>restore</c> subcommand against a real running compose stack
/// inside the shared Ubuntu DinD fixture. Each test runs a full <c>bootstrap</c>, inserts
/// a marker row, takes a backup, mutates or drops the marker, then restores and asserts
/// the marker is back to its pre-mutation state.
/// </summary>
/// <remarks>
/// <para>
/// The postgres marker uses a dedicated table under the <c>test_pg_db</c> database so we
/// don't have to guess at the API's schema. <c>pg_restore --clean --if-exists</c> drops and
/// recreates the table as part of the restore.
/// </para>
/// <para>
/// The scylla assertions focus on the stack-coordination side effects (containers stopped,
/// data volume overwritten, containers back up) rather than on CQL row-level round-trips
/// — the fixture's config uses <c>databaseMode=single</c> where the API isn't wired to
/// exercise a scylla-side round-trip via the schema, and the pure "restore mechanic"
/// contract (compose stop → wipe → cp → start) is what changed relative to backup.
/// </para>
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class RestorePhaseTests(UbuntuDinDFixture dinD)
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
    public async Task RestorePostgresFromDumpRoundTripsMarkerTable()
    {
        // 1. Bootstrap → live stack. 2. Create a marker table + row via psql (using the
        // admin credentials from secrets.json). 3. Take a backup. 4. DROP the table.
        // 5. Restore --restore-postgres --force. 6. Assert the marker table + row are back.
        var scratch = await dinD.CreateScratchAsync(nameof(RestorePostgresFromDumpRoundTripsMarkerTable), TestConfigJsonPath);
        var composeFile = $"{scratch.OutputDir}/docker-compose.yaml";

        var bootstrap = await dinD.RunBootstrapperAsync($"{nameof(RestorePostgresFromDumpRoundTripsMarkerTable)}-bootstrap",
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--non-interactive", "--skip-prereqs"]);
        await Assert.That(bootstrap.ExitCode).IsEqualTo(0).Because($"bootstrap failed: {bootstrap.Stderr}");

        // Seed a marker table into the test DB using the admin role from secrets.json.
        // Extracting the admin password from the JSON via `grep + sed` — keeps the test
        // self-contained without needing to parse secrets.json in the test process.
        var seed = await dinD.ExecAsync(["sh", "-c",
            $"ADMIN_PW=$(grep postgresAdminPassword {scratch.OutputDir}/secrets/secrets.json | " +
            "sed -E 's/.*\"([^\"]+)\".*$/\\1/' | tail -1); " +
            $"docker compose -f {composeFile} exec -T -e PGPASSWORD=\"$ADMIN_PW\" msg-db " +
            "psql -U interfold_admin -d test_pg_db -c " +
            "\"CREATE TABLE restore_marker(id int primary key); INSERT INTO restore_marker(id) VALUES (42);\""]);
        await Assert.That(seed.ExitCode).IsEqualTo(0L)
            .Because($"seeding marker table failed: {seed.Stderr}");

        // Take the backup that we'll restore from.
        var backup = await dinD.RunBootstrapperAsync($"{nameof(RestorePostgresFromDumpRoundTripsMarkerTable)}-backup",
            ["backup", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--component", "postgres", "--non-interactive"]);
        await Assert.That(backup.ExitCode).IsEqualTo(0).Because($"backup failed: {backup.Stderr}");

        // Drop the marker table so the restore path has something concrete to bring back.
        var drop = await dinD.ExecAsync(["sh", "-c",
            $"ADMIN_PW=$(grep postgresAdminPassword {scratch.OutputDir}/secrets/secrets.json | " +
            "sed -E 's/.*\"([^\"]+)\".*$/\\1/' | tail -1); " +
            $"docker compose -f {composeFile} exec -T -e PGPASSWORD=\"$ADMIN_PW\" msg-db " +
            "psql -U interfold_admin -d test_pg_db -c \"DROP TABLE restore_marker;\""]);
        await Assert.That(drop.ExitCode).IsEqualTo(0L)
            .Because($"dropping the marker table failed: {drop.Stderr}");

        // Confirm the table is really gone before the restore — otherwise we'd be
        // testing a no-op path and the assertion below would pass vacuously.
        var missing = await dinD.ExecAsync(["sh", "-c",
            $"ADMIN_PW=$(grep postgresAdminPassword {scratch.OutputDir}/secrets/secrets.json | " +
            "sed -E 's/.*\"([^\"]+)\".*$/\\1/' | tail -1); " +
            $"docker compose -f {composeFile} exec -T -e PGPASSWORD=\"$ADMIN_PW\" msg-db " +
            "psql -U interfold_admin -d test_pg_db -tAc \"SELECT to_regclass('public.restore_marker') IS NULL\""]);
        await Assert.That(missing.Stdout.Trim()).IsEqualTo("t")
            .Because("marker table must actually be absent before the restore");

        // Restore --restore-latest picks up the .dump we just took. --force skips the
        // destructive-op confirmation prompt (required in --non-interactive mode).
        var restore = await dinD.RunBootstrapperAsync(nameof(RestorePostgresFromDumpRoundTripsMarkerTable),
            ["restore", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--restore-latest", "--force", "--non-interactive"]);
        await Assert.That(restore.ExitCode).IsEqualTo(0).Because($"restore failed: {restore.Stderr}");

        // The marker must be back. Value check pins that the round-trip preserved the
        // row data, not just the schema.
        var probe = await dinD.ExecAsync(["sh", "-c",
            $"ADMIN_PW=$(grep postgresAdminPassword {scratch.OutputDir}/secrets/secrets.json | " +
            "sed -E 's/.*\"([^\"]+)\".*$/\\1/' | tail -1); " +
            $"docker compose -f {composeFile} exec -T -e PGPASSWORD=\"$ADMIN_PW\" msg-db " +
            "psql -U interfold_admin -d test_pg_db -tAc \"SELECT id FROM restore_marker\""]);
        await Assert.That(probe.ExitCode).IsEqualTo(0L)
            .Because($"post-restore probe failed: {probe.Stderr}");
        await Assert.That(probe.Stdout.Trim()).IsEqualTo("42")
            .Because("restore must have re-created the marker table with its original row");
    }

    [Test]
    public async Task RestoreLatestPicksMostRecentArchive()
    {
        // Take two backups with a delay between them, then --restore-latest must resolve
        // to the newer archive by mtime. The log lines from RestorePhase.ResolveArchives
        // name the chosen path, giving us a deterministic assertion target without
        // having to poke inside the running database.
        var scratch = await dinD.CreateScratchAsync(nameof(RestoreLatestPicksMostRecentArchive), TestConfigJsonPath);

        var bootstrap = await dinD.RunBootstrapperAsync($"{nameof(RestoreLatestPicksMostRecentArchive)}-bootstrap",
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--non-interactive", "--skip-prereqs"]);
        await Assert.That(bootstrap.ExitCode).IsEqualTo(0).Because($"bootstrap failed: {bootstrap.Stderr}");

        // First backup, then a 2s sleep so mtimes are distinguishable at second
        // resolution, then a second backup.
        var b1 = await dinD.RunBootstrapperAsync($"{nameof(RestoreLatestPicksMostRecentArchive)}-b1",
            ["backup", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--component", "postgres", "--non-interactive"]);
        await Assert.That(b1.ExitCode).IsEqualTo(0).Because(b1.Stderr);

        await dinD.ExecAsync(["sleep", "2"]);

        var b2 = await dinD.RunBootstrapperAsync($"{nameof(RestoreLatestPicksMostRecentArchive)}-b2",
            ["backup", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--component", "postgres", "--non-interactive"]);
        await Assert.That(b2.ExitCode).IsEqualTo(0).Because(b2.Stderr);

        // Resolve the newest archive name via a shell one-liner so we know what the
        // restore log should reference.
        var newestList = await dinD.ExecAsync(["sh", "-c",
            $"ls -1t {scratch.OutputDir}/backups/postgres/*.dump | head -1"]);
        await Assert.That(newestList.ExitCode).IsEqualTo(0L);
        var newestArchive = newestList.Stdout.Trim();
        await Assert.That(newestArchive.Length).IsGreaterThan(0)
            .Because("there must be at least one archive present after two backup runs");

        // Restore with --restore-latest and no explicit --restore-postgres so the
        // resolver picks by mtime. The log line names the chosen path — assert it.
        var restore = await dinD.RunBootstrapperAsync(nameof(RestoreLatestPicksMostRecentArchive),
            ["restore", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--restore-latest", "--force", "--non-interactive"]);
        await Assert.That(restore.ExitCode).IsEqualTo(0).Because($"restore failed: {restore.Stderr}");

        var combined = restore.Stdout + restore.Stderr;
        await Assert.That(combined).Contains(Path.GetFileName(newestArchive))
            .Because("--restore-latest must resolve to the newest archive by mtime");
    }

    [Test]
    public async Task RestoreWithoutArchivesFailsClearly()
    {
        // Zero-archive-selector case: --restore-latest by itself against an
        // empty {backups}/ directory must produce an actionable error naming the
        // missing selectors, not crash somewhere deep in pg_restore.
        var scratch = await dinD.CreateScratchAsync(nameof(RestoreWithoutArchivesFailsClearly), TestConfigJsonPath);

        var bootstrap = await dinD.RunBootstrapperAsync($"{nameof(RestoreWithoutArchivesFailsClearly)}-bootstrap",
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--non-interactive", "--skip-prereqs"]);
        await Assert.That(bootstrap.ExitCode).IsEqualTo(0).Because($"bootstrap failed: {bootstrap.Stderr}");

        var result = await dinD.RunBootstrapperAsync(nameof(RestoreWithoutArchivesFailsClearly),
            ["restore", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--restore-latest", "--force", "--non-interactive"]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("restore --restore-latest with no archives on disk must fail");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("restore-postgres").Or.Contains("restore-scylla").Or.Contains("archives")
            .Because("error must name the missing selectors or archives");
    }

    [Test]
    public async Task RestoreInNonInteractiveModeRequiresForce()
    {
        // Destructive-op safety gate. Passing a real archive selector without --force
        // in --non-interactive mode must refuse to proceed and print a clear error
        // pointing the operator at the --force flag.
        var scratch = await dinD.CreateScratchAsync(nameof(RestoreInNonInteractiveModeRequiresForce), TestConfigJsonPath);

        var bootstrap = await dinD.RunBootstrapperAsync($"{nameof(RestoreInNonInteractiveModeRequiresForce)}-bootstrap",
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--non-interactive", "--skip-prereqs"]);
        await Assert.That(bootstrap.ExitCode).IsEqualTo(0).Because($"bootstrap failed: {bootstrap.Stderr}");

        // Take a backup so there's a real archive to point at.
        var backup = await dinD.RunBootstrapperAsync($"{nameof(RestoreInNonInteractiveModeRequiresForce)}-backup",
            ["backup", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--component", "postgres", "--non-interactive"]);
        await Assert.That(backup.ExitCode).IsEqualTo(0).Because(backup.Stderr);

        // No --force. Must fail with a clear error mentioning --force.
        var result = await dinD.RunBootstrapperAsync(nameof(RestoreInNonInteractiveModeRequiresForce),
            ["restore", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--restore-latest", "--non-interactive"]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("restore in non-interactive mode without --force must be refused");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("--force")
            .Because("error must name --force as the escape hatch for non-interactive mode");
    }
}
