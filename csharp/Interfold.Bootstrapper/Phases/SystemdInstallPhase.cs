using System.Text;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Installs (and optionally enables) the three systemd units that own the boot-up
/// autostart + scheduled-backup wiring:
/// <list type="bullet">
///   <item><c>interfold.service</c> — Type=oneshot RemainAfterExit=yes; brings the compose
///         stack up via <c>docker compose -f {outputDir}/docker-compose.yaml up -d</c>.</item>
///   <item><c>interfold-backup.service</c> — Type=oneshot; runs
///         <c>interfold-bootstrap backup</c>.</item>
///   <item><c>interfold-backup.timer</c> — fires the backup service on the schedule from
///         <see cref="BackupSection.Schedule"/>.</item>
/// </list>
/// Templates ship as <c>systemd/*</c> manifest resources (see Interfold.Bootstrapper.csproj
/// — distinct from the <c>support/</c> prefix consumed by
/// <see cref="Util.EmbeddedSupportFiles"/>) so the unrendered template strings never land
/// on disk where an operator might enable them by mistake.
/// </summary>
internal static class SystemdInstallPhase
{
    private const string Phase = "install-service";

    /// <summary>Default systemd unit installation directory on every supported distro.</summary>
    private const string DefaultUnitDir = "/etc/systemd/system";

    /// <summary>Default basename for the bootstrapper binary inside the install dir.</summary>
    private const string DefaultBinaryName = "interfold-bootstrap";

    /// <summary>Names of the three units this phase installs, in install order.</summary>
    internal static readonly string[] UnitNames =
    [
        "interfold.service",
        "interfold-backup.service",
        "interfold-backup.timer",
    ];

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var configPath = options.ConfigPath ?? Path.Combine(options.OutputDir, "interfold.bootstrap.json");
        if (!File.Exists(configPath))
        {
            logger.PhaseFail(Phase, "missing-config");
            throw new InvalidOperationException(
                $"install-service requires a populated bootstrap config at {configPath}. " +
                "Run `bootstrap` first.");
        }

        BootstrapConfig config;
        await using (var stream = File.OpenRead(configPath))
        {
            config = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to parse {configPath}.");
        }

        var unitDir = options.SystemdUnitDir ?? DefaultUnitDir;
        var binaryPath = ResolveBinaryPath(options);
        var composeFile = ResolveComposeFile(options);

        var renderInput = new SystemdRenderInput(
            OutputDir: options.OutputDir,
            ComposeFile: composeFile,
            ConfigPath: Path.GetFullPath(configPath),
            BinaryPath: binaryPath,
            OnCalendar: config.Backup.Schedule);

        logger.Info($"    rendering units to {unitDir}");
        Directory.CreateDirectory(unitDir);

        foreach (var unitName in UnitNames)
        {
            var rendered = RenderUnit(unitName, renderInput);
            var destination = Path.Combine(unitDir, unitName);
            await File.WriteAllTextAsync(destination, rendered, ct).ConfigureAwait(false);
            logger.Info($"    wrote {destination}");
        }

        // systemd-analyze verify catches typos, missing tokens, and invalid directives
        // before we ever hand the unit to systemd. Skip silently when the binary isn't
        // available (tests on a docker-less Windows host) so the renderer path still has
        // coverage; production hosts always ship systemd-analyze with the systemd package.
        if (await ProcessRunner.ExistsOnPathAsync("systemd-analyze", ct).ConfigureAwait(false))
        {
            await VerifyAllUnitsAsync(unitDir, logger, ct).ConfigureAwait(false);
            await VerifyCalendarAsync(config.Backup.Schedule, logger, ct).ConfigureAwait(false);
        }
        else
        {
            logger.Warn("systemd-analyze not on PATH; skipping unit verification " +
                        "(install the systemd package on the target host).");
        }

        // The enable decision is layered: explicit --enable-* flags win, then fall back to
        // the matching config toggles. This means an operator who set
        // backup.enabled=true in interfold.bootstrap.json gets the timer enabled on a
        // plain `install-service` invocation, while a CI test passing --systemd-unit-dir
        // for verification only can omit the enable flags entirely.
        var enableAutostart = options.EnableAutostart || config.Backup.AutostartServer;
        var enableBackupTimer = options.EnableBackupTimer || config.Backup.Enabled;

        if (await ProcessRunner.ExistsOnPathAsync("systemctl", ct).ConfigureAwait(false)
            && options.SystemdUnitDir is null)
        {
            // Only daemon-reload and enable when writing to the real /etc/systemd/system/.
            // The test path (--systemd-unit-dir=<tmp>) skips this so it doesn't perturb the
            // host's systemd state.
            await SystemctlAsync(["daemon-reload"], logger, ct).ConfigureAwait(false);

            if (enableAutostart)
            {
                logger.Info("    enabling interfold.service");
                await SystemctlAsync(["enable", "--now", "interfold.service"], logger, ct).ConfigureAwait(false);
            }
            if (enableBackupTimer)
            {
                logger.Info("    enabling interfold-backup.timer");
                await SystemctlAsync(["enable", "--now", "interfold-backup.timer"], logger, ct).ConfigureAwait(false);
            }
        }
        else if (options.SystemdUnitDir is not null)
        {
            logger.Info("    --systemd-unit-dir set; skipping daemon-reload/enable (test mode)");
        }
        else
        {
            logger.Warn("systemctl not on PATH; units written but not enabled. " +
                        "Run `systemctl daemon-reload` then `systemctl enable --now <unit>` manually.");
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>
    /// Token-substitution input bundle. Plain record so the unit-test project can drive
    /// <see cref="RenderUnit"/> against bespoke inputs without staging a real
    /// <see cref="BootstrapOptions"/>.
    /// </summary>
    internal sealed record SystemdRenderInput(
        string OutputDir,
        string ComposeFile,
        string ConfigPath,
        string BinaryPath,
        string OnCalendar);

    /// <summary>
    /// Reads the embedded template for <paramref name="unitName"/>, replaces every
    /// <c>{{TOKEN}}</c> with the matching field from <paramref name="input"/>, and returns
    /// the rendered text. Internal so the renderer can be unit-tested in isolation from
    /// the install path (which writes to disk + shells out to systemctl).
    /// </summary>
    internal static string RenderUnit(string unitName, SystemdRenderInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);
        ArgumentNullException.ThrowIfNull(input);

        var resourceName = $"systemd/{unitName}";
        var asm = typeof(SystemdInstallPhase).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded systemd template '{resourceName}' missing. " +
                "Check Interfold.Bootstrapper.csproj's <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var template = reader.ReadToEnd();

        var sb = new StringBuilder(template);
        // Order matters: the renderer must accept every token the bundled templates
        // reference. If a future template adds a new token without a matching replacement
        // here, the rendered file will still contain a literal "{{NEW_TOKEN}}" and
        // systemd-analyze will catch it — but better to fail fast with a clear C#
        // exception. SanityCheck below enforces that contract.
        sb.Replace("{{OUTPUT_DIR}}", input.OutputDir);
        sb.Replace("{{COMPOSE_FILE}}", input.ComposeFile);
        sb.Replace("{{CONFIG_PATH}}", input.ConfigPath);
        sb.Replace("{{BINARY_PATH}}", input.BinaryPath);
        sb.Replace("{{ON_CALENDAR}}", input.OnCalendar);
        var rendered = sb.ToString();

        if (rendered.Contains("{{", StringComparison.Ordinal))
        {
            // Strip the noise around the residual token for a tighter error message.
            var openIdx = rendered.IndexOf("{{", StringComparison.Ordinal);
            var closeIdx = rendered.IndexOf("}}", openIdx, StringComparison.Ordinal);
            var snippet = closeIdx > openIdx
                ? rendered.Substring(openIdx, closeIdx - openIdx + 2)
                : rendered[openIdx..Math.Min(rendered.Length, openIdx + 40)];
            throw new InvalidOperationException(
                $"systemd template '{unitName}' contains an unsubstituted token '{snippet}'. " +
                "Add the matching replacement to SystemdInstallPhase.RenderUnit.");
        }

        return rendered;
    }

    private static string ResolveBinaryPath(BootstrapOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BinaryPathOverride))
        {
            return Path.GetFullPath(options.BinaryPathOverride);
        }
        // The published binary is `interfold-bootstrap` (AssemblyName in the csproj). When
        // the bootstrapper is invoked via `dotnet ./interfold-bootstrap.dll` we still want
        // to point the systemd unit at the .NET-loaded entry-point binary alongside that
        // dll; AppContext.BaseDirectory + "interfold-bootstrap" is the canonical install
        // layout the README documents.
        return Path.Combine(AppContext.BaseDirectory, DefaultBinaryName);
    }

    private static string ResolveComposeFile(BootstrapOptions options)
    {
        var direct = Path.Combine(options.OutputDir, "docker-compose.yaml");
        if (File.Exists(direct)) return Path.GetFullPath(direct);
        var nested = Directory.EnumerateFiles(options.OutputDir, "docker-compose.yaml", SearchOption.AllDirectories)
            .FirstOrDefault();
        // Fall back to the conventional path even if the file doesn't exist yet — install-service
        // should be runnable before the first `bootstrap` (some operators want the unit files
        // pre-populated for image-baking workflows). The boot-time `docker compose up` will
        // surface the missing-file error if the operator never runs the publish phase.
        return nested is not null ? Path.GetFullPath(nested) : Path.GetFullPath(direct);
    }

    private static async Task VerifyAllUnitsAsync(string unitDir, PhaseLogger logger, CancellationToken ct)
    {
        foreach (var unitName in UnitNames)
        {
            var unitPath = Path.Combine(unitDir, unitName);
            var verify = await ProcessRunner.RunAsync(
                "systemd-analyze", ["verify", unitPath], ct: ct).ConfigureAwait(false);
            if (verify.ExitCode != 0)
            {
                logger.PhaseFail(Phase, "systemd-analyze-verify");
                throw new InvalidOperationException(
                    $"systemd-analyze verify failed for {unitPath} (exit {verify.ExitCode}):\n" +
                    $"{verify.StdErr.Trim()}\n{verify.StdOut.Trim()}");
            }
            logger.Info($"    verified {unitName}");
        }
    }

    private static async Task VerifyCalendarAsync(string schedule, PhaseLogger logger, CancellationToken ct)
    {
        var verify = await ProcessRunner.RunAsync(
            "systemd-analyze", ["calendar", schedule], ct: ct).ConfigureAwait(false);
        if (verify.ExitCode != 0)
        {
            logger.PhaseFail(Phase, "invalid-calendar");
            throw new InvalidOperationException(
                $"systemd-analyze calendar '{schedule}' rejected the schedule (exit {verify.ExitCode}):\n" +
                $"{verify.StdErr.Trim()}\n{verify.StdOut.Trim()}");
        }
        logger.Info($"    calendar '{schedule}' parses OK");
    }

    private static async Task SystemctlAsync(IReadOnlyList<string> args, PhaseLogger logger, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync("systemctl", args, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            logger.PhaseFail(Phase, "systemctl");
            throw new InvalidOperationException(
                $"systemctl {string.Join(' ', args)} exited {run.ExitCode}: {run.StdErr.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(run.StdOut)) logger.Info(run.StdOut.Trim());
    }

}
