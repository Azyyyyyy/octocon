using System.Globalization;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// One-shot logical backup of the live Postgres + Scylla/Cassandra state. Runs after
/// <see cref="LaunchPhase"/> has the stack up; idempotent across reruns. Driven either
/// manually by the operator (<c>interfold-bootstrap backup</c>) or unattended by the
/// systemd timer installed via <see cref="SystemdInstallPhase"/>.
/// </summary>
/// <remarks>
/// <para>
/// Backup layout (per component) is <c>{backupDir}/{postgres|scylla}/{timestamp}.{ext}</c>
/// where:
/// <list type="bullet">
///   <item><c>backupDir</c> resolves to the operator-supplied path, otherwise
///         <c>{outputDir}/backups</c>.</item>
///   <item><c>postgres</c>: <c>.dump</c> custom-format pg_dump (restorable via
///         <c>pg_restore</c>). Authenticates as the <c>{user}_admin</c> role created by
///         <see cref="DatabaseInitPhase"/>; password sourced from
///         <c>secrets/secrets.json</c> via the <c>PGPASSWORD</c> env var so it never
///         appears on the docker argv.</item>
///   <item><c>scylla</c>: <c>.tar.gz</c> of <c>nodetool snapshot</c> contents from the
///         seed node only. Multi-DC operators that want all seven regional nodes captured
///         do that with their own wrapper — documented in <see cref="DatabaseMode"/>.</item>
/// </list>
/// </para>
/// <para>
/// Retention: after a successful write the phase walks <c>{backupDir}/{component}/</c> and
/// deletes the oldest archives (by file mtime) until exactly <c>RetainCount</c> remain.
/// The pure pruning logic lives in <see cref="BackupRetention"/> so the unit tests can
/// drive every edge case without staging real files.
/// </para>
/// </remarks>
internal static class BackupPhase
{
    private const string Phase = "backup";
    private const string PostgresService = "msg-db";

    /// <summary>
    /// Allowed values for <see cref="BootstrapOptions.BackupComponent"/>. Kept as an array
    /// so the validator can surface the canonical set in its error message.
    /// </summary>
    internal static readonly string[] ValidComponents = ["postgres", "scylla", "all"];

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var component = options.BackupComponent?.ToLowerInvariant() ?? "all";
        if (!ValidComponents.Contains(component, StringComparer.Ordinal))
        {
            logger.PhaseFail(Phase, "unknown-component");
            throw new InvalidOperationException(
                $"--component='{options.BackupComponent}' is invalid. Expected one of: {string.Join(", ", ValidComponents)}.");
        }

        var configPath = options.ConfigPath ?? Path.Combine(options.OutputDir, "interfold.bootstrap.json");
        if (!File.Exists(configPath))
        {
            logger.PhaseFail(Phase, "missing-config");
            throw new InvalidOperationException(
                $"Backup requires a populated bootstrap config at {configPath}. " +
                "Run `bootstrap` first.");
        }

        BootstrapConfig config;
        await using (var stream = File.OpenRead(configPath))
        {
            config = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to parse {configPath}.");
        }

        GeneratedSecrets secrets;
        try
        {
            secrets = SecretsPhase.LoadExisting(options);
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw with a phase-specific message; the original carries the secrets-phase
            // wording which is misleading in a backup context.
            logger.PhaseFail(Phase, "missing-secrets");
            throw new InvalidOperationException(
                $"Backup requires the admin credentials in secrets/secrets.json under {options.OutputDir}. " +
                "Run `bootstrap` first to generate them.", ex);
        }

        var composeFile = FindComposeFile(options.OutputDir);
        if (composeFile is null)
        {
            logger.PhaseFail(Phase, "no-compose-file");
            throw new InvalidOperationException(
                $"docker-compose.yaml not found under {options.OutputDir}. Run `bootstrap publish` first.");
        }
        logger.Info($"    using compose file {composeFile}");

        var backupRoot = ResolveBackupRoot(options, config);
        var retainCount = options.BackupRetainOverride ?? config.Backup.RetainCount;
        if (retainCount < 1)
        {
            logger.PhaseFail(Phase, "invalid-retain");
            throw new InvalidOperationException(
                $"--retain={retainCount} is below the minimum of 1.");
        }
        logger.Info($"    backup root: {backupRoot} (retain {retainCount} per component)");

        // Stable timestamp shared by every artifact this run produces, so a postgres+scylla
        // pair can be correlated by filename without parsing inside-the-file metadata.
        // ISO-ish, sortable, no separators that need escaping on a Unix filesystem.
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (component is "postgres" or "all")
        {
            await BackupPostgresAsync(composeFile, backupRoot, timestamp, config, secrets, retainCount, logger, ct)
                .ConfigureAwait(false);
        }

        if (component is "scylla" or "all")
        {
            await BackupScyllaAsync(composeFile, backupRoot, timestamp, config, retainCount, logger, ct)
                .ConfigureAwait(false);
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>
    /// Resolves the on-disk backup root for this invocation. Precedence:
    /// <list type="number">
    ///   <item><c>--backup-dir</c> on the CLI (operator escape hatch for one-shot runs).</item>
    ///   <item><c>config.backup.directory</c> when non-empty (the systemd-timer path).</item>
    ///   <item>Default: <c>{outputDir}/backups</c>.</item>
    /// </list>
    /// Internal so the unit tests can assert the precedence rules without driving the full phase.
    /// </summary>
    internal static string ResolveBackupRoot(BootstrapOptions options, BootstrapConfig config)
    {
        if (!string.IsNullOrWhiteSpace(options.BackupDirOverride))
        {
            return Path.GetFullPath(options.BackupDirOverride);
        }
        if (!string.IsNullOrWhiteSpace(config.Backup.Directory))
        {
            return Path.GetFullPath(config.Backup.Directory);
        }
        return Path.Combine(options.OutputDir, "backups");
    }

    /// <summary>
    /// Resolves the Scylla/Cassandra seed service name + container-side data path for the
    /// configured <see cref="BootstrapConfig.DatabaseMode"/>. Mirrors the resource-naming
    /// rules in <c>InterfoldAppHost.Configure</c> so the docker-compose exec lands on the
    /// same container the AppHost graph spun up. Internal for unit testing.
    /// </summary>
    internal static (string Service, string DataPath) ResolveScyllaSeed(BootstrapConfig config)
    {
        return config.DatabaseMode switch
        {
            "cassandra" => ("cassandra", "/var/lib/cassandra"),
            "multi" => ("scylla-nam", "/var/lib/scylla"),
            _ => ("scylla", "/var/lib/scylla"),
        };
    }

    /// <summary>
    /// Computes the canonical archive filename for a given component + timestamp. Returned
    /// path is relative to the component subdirectory; callers join with the backup root.
    /// </summary>
    internal static string BuildArchiveFileName(string component, string timestamp)
    {
        return component switch
        {
            "postgres" => $"{timestamp}.dump",
            "scylla" => $"{timestamp}.tar.gz",
            _ => throw new InvalidOperationException($"Unknown component '{component}' (expected: postgres | scylla)."),
        };
    }

    /// <summary>
    /// Builds the docker-compose exec argv for the Postgres backup probe. Internal so the
    /// unit-test project can assert the argv shape without invoking docker. The
    /// <c>PGPASSWORD</c> env var is set on the exec via <c>--env</c> so the admin password
    /// never appears on the process argv (which would otherwise be visible to anyone with
    /// <c>ps</c>).
    /// </summary>
    internal static IReadOnlyList<string> BuildPostgresDumpArgs(
        string composeFile, string adminUser, string database)
    {
        return
        [
            "compose", "-f", composeFile,
            "exec", "-T",
            "--env", "PGPASSWORD",
            PostgresService,
            "pg_dump",
            "-U", adminUser,
            "-d", database,
            "-Fc",
        ];
    }

    /// <summary>
    /// Builds the docker-compose exec argv for the Scylla/Cassandra snapshot+tar pipeline.
    /// Returns three argv lists (snapshot, tar, clearsnapshot) so the caller can run each
    /// phase with the right stdout redirection (only the middle one streams the tarball).
    /// Internal so unit tests can assert the argv shape without invoking docker.
    /// </summary>
    internal static (IReadOnlyList<string> Snapshot, IReadOnlyList<string> Tar, IReadOnlyList<string> Clear)
        BuildScyllaSnapshotArgs(string composeFile, string service, string dataPath, string tag)
    {
        var snapshot = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "nodetool", "snapshot", "-t", tag,
        };
        // tar -C to the data dir so the archive is relative-pathed (operator can untar
        // straight into a fresh /var/lib/scylla without stripping leading components).
        // -c writes to stdout (the leading -); docker compose exec forwards that to the
        // process's stdout, which we redirect into the .tar.gz file on the host.
        var tar = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "tar", "-C", dataPath, "-czf", "-", ".",
        };
        var clear = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "nodetool", "clearsnapshot", "-t", tag,
        };
        return (snapshot, tar, clear);
    }

    private static async Task BackupPostgresAsync(
        string composeFile, string backupRoot, string timestamp,
        BootstrapConfig config, GeneratedSecrets secrets, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        var componentDir = Path.Combine(backupRoot, "postgres");
        Directory.CreateDirectory(componentDir);
        var dumpPath = Path.Combine(componentDir, BuildArchiveFileName("postgres", timestamp));

        // Admin role created by DatabaseInitPhase. We don't try to run the dump as the app
        // role — pg_dump needs broader privileges to capture every object regardless of
        // ownership, and the admin role is exactly what `DatabaseInitPhase.BuildPostgresSeedOptions`
        // already provisioned for that purpose (see csharp/Interfold.Bootstrapper/Phases/DatabaseInitPhase.cs).
        var adminUser = $"{secrets.PostgresUser}_admin";
        var adminPassword = secrets.PostgresAdminPassword;
        if (string.IsNullOrEmpty(adminPassword))
        {
            logger.PhaseFail(Phase, "missing-admin-password");
            throw new InvalidOperationException(
                "secrets/secrets.json does not contain a PostgresAdminPassword. " +
                "Either it predates DatabaseInitPhase or it was hand-edited; re-run `bootstrap`.");
        }

        logger.Info($"    postgres: pg_dump -> {dumpPath}");
        var argv = BuildPostgresDumpArgs(composeFile, adminUser, config.PostgresDatabase);
        var env = new Dictionary<string, string?> { ["PGPASSWORD"] = adminPassword };

        // Stream stdout straight to the output file so the dump never sits in memory.
        await StreamProcessStdoutToFileAsync("docker", argv, env, dumpPath, ct).ConfigureAwait(false);

        var size = new FileInfo(dumpPath).Length;
        if (size == 0)
        {
            logger.PhaseFail(Phase, "empty-postgres-dump");
            File.Delete(dumpPath);
            throw new InvalidOperationException(
                $"pg_dump produced an empty file at {dumpPath}. Inspect docker logs for {PostgresService}.");
        }
        logger.Info($"    postgres: wrote {FormatBytes(size)}");

        PruneComponent(componentDir, "postgres", retainCount, logger);
    }

    private static async Task BackupScyllaAsync(
        string composeFile, string backupRoot, string timestamp,
        BootstrapConfig config, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        var (service, dataPath) = ResolveScyllaSeed(config);
        var componentDir = Path.Combine(backupRoot, "scylla");
        Directory.CreateDirectory(componentDir);
        var archivePath = Path.Combine(componentDir, BuildArchiveFileName("scylla", timestamp));

        // Snapshot tag pinned to the timestamp so a failed clear (e.g. compose down between
        // snapshot and clear) leaves an obvious orphan an operator can match to the failed
        // run. The tag is purely a name on disk inside the container.
        var tag = $"interfold-backup-{timestamp}";

        var (snapshotArgs, tarArgs, clearArgs) = BuildScyllaSnapshotArgs(composeFile, service, dataPath, tag);

        logger.Info($"    scylla: nodetool snapshot -t {tag} (service={service})");
        var snapshot = await ProcessRunner.RunAsync("docker", snapshotArgs, ct: ct).ConfigureAwait(false);
        if (snapshot.ExitCode != 0)
        {
            logger.PhaseFail(Phase, "nodetool-snapshot");
            throw new InvalidOperationException(
                $"nodetool snapshot exited {snapshot.ExitCode} on {service}: {snapshot.StdErr.Trim()}");
        }

        logger.Info($"    scylla: tar -czf - {dataPath} -> {archivePath}");
        try
        {
            await StreamProcessStdoutToFileAsync("docker", tarArgs, environment: null, archivePath, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // Best-effort clearsnapshot regardless of tar success/failure — leftover
            // snapshots eat disk on the scylla container indefinitely otherwise. Failure
            // here is logged but doesn't fail the phase (the tar already succeeded or
            // already failed; the dispositive verdict is the operator-visible archive).
            var clear = await ProcessRunner.RunAsync("docker", clearArgs, ct: ct).ConfigureAwait(false);
            if (clear.ExitCode != 0)
            {
                logger.Warn($"nodetool clearsnapshot -t {tag} exited {clear.ExitCode}: {clear.StdErr.Trim()}");
            }
        }

        var size = new FileInfo(archivePath).Length;
        if (size == 0)
        {
            logger.PhaseFail(Phase, "empty-scylla-archive");
            File.Delete(archivePath);
            throw new InvalidOperationException(
                $"Scylla tar produced an empty file at {archivePath}. Inspect docker logs for {service}.");
        }
        logger.Info($"    scylla: wrote {FormatBytes(size)}");

        PruneComponent(componentDir, "scylla", retainCount, logger);
    }

    private static void PruneComponent(string componentDir, string component, int retainCount, PhaseLogger logger)
    {
        var pattern = component switch
        {
            "postgres" => "*.dump",
            "scylla" => "*.tar.gz",
            _ => throw new InvalidOperationException($"Unknown component '{component}'."),
        };
        var files = new DirectoryInfo(componentDir)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .ToList();

        foreach (var stale in BackupRetention.Prune(files, retainCount))
        {
            try
            {
                stale.Delete();
                logger.Info($"    {component}: pruned {stale.Name}");
            }
            catch (Exception ex)
            {
                logger.Warn($"failed to delete {stale.FullName}: {ex.Message}");
            }
        }
    }

    private static string? FindComposeFile(string outputDir)
    {
        var direct = Path.Combine(outputDir, "docker-compose.yaml");
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(outputDir, "docker-compose.yaml", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and streams its
    /// stdout directly into <paramref name="destinationPath"/>. Throws if the process exits
    /// non-zero; partial output is preserved on disk for diagnosis but the caller deletes
    /// it before propagating the exception when it's known to be empty/corrupt.
    /// </summary>
    private static async Task StreamProcessStdoutToFileAsync(
        string fileName, IReadOnlyList<string> arguments,
        IDictionary<string, string?>? environment, string destinationPath,
        CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }
        proc.BeginErrorReadLine();

        try
        {
            await using (var fs = File.Create(destinationPath))
            {
                await proc.StandardOutput.BaseStream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} exited {proc.ExitCode}: {stderr.ToString().Trim()}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        // KiB/MiB/GiB — operator-facing diagnostic only, no need to nail down the unit edge
        // cases or culture-specific formatting beyond invariant.
        const long Kib = 1024L;
        const long Mib = Kib * 1024L;
        const long Gib = Mib * 1024L;
        if (bytes >= Gib) return $"{bytes / (double)Gib:F2} GiB";
        if (bytes >= Mib) return $"{bytes / (double)Mib:F2} MiB";
        if (bytes >= Kib) return $"{bytes / (double)Kib:F2} KiB";
        return $"{bytes} B";
    }
}

/// <summary>
/// Pure pruning helper: given an unordered set of backup files and a target retention
/// count, returns the files to delete (oldest by last-write time). Extracted out of
/// <see cref="BackupPhase"/> so the retention semantics can be exhaustively unit-tested
/// without staging real files on disk.
/// </summary>
internal static class BackupRetention
{
    /// <summary>
    /// Selects the files to delete so the surviving set has exactly <paramref name="keep"/>
    /// entries. When the input has &lt;= <paramref name="keep"/> files, returns an empty
    /// sequence. Ordering of the returned files is oldest-first.
    /// </summary>
    /// <param name="files">Candidate backup archives in the component subdirectory.</param>
    /// <param name="keep">Target survivor count. Must be &gt;= 0; zero deletes everything.</param>
    public static IEnumerable<FileInfo> Prune(IEnumerable<FileInfo> files, int keep)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "retain count must be >= 0.");
        }

        // Materialise once — both the count and the order matter, and the caller hands us a
        // freshly-enumerated DirectoryInfo result that doesn't survive a second pass anyway.
        var ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
        var toDeleteCount = ordered.Count - keep;
        return toDeleteCount <= 0
            ? []
            : ordered.Take(toDeleteCount);
    }
}
