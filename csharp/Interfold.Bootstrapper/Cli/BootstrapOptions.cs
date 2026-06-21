namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// Process-wide invocation options resolved from the CLI. Phases receive this record and
/// dispatch based on the flags rather than reading global state.
/// </summary>
/// <param name="Command">Which subcommand was invoked.</param>
/// <param name="ConfigPath">Path to <c>interfold.bootstrap.json</c>. If null, the bootstrapper prompts interactively (in tty mode).</param>
/// <param name="OutputDir">Root directory for emitted artifacts (compose, certs, secrets).</param>
/// <param name="SkipPrereqs">If true, the prerequisites phase is bypassed. Useful for re-runs after Docker is installed.</param>
/// <param name="RotateSecrets">If true, the secrets phase regenerates everything even if a secrets file already exists.</param>
/// <param name="RotateCerts">If true, the certificate phase regenerates the root CA and leaf cert.</param>
/// <param name="NonInteractive">If true, missing config values are an error rather than a prompt.</param>
/// <param name="FaultInject">Hidden testability hook: exits 1 immediately after the named phase (e.g. <c>after-secrets</c>).</param>
/// <param name="PrintPhaseStatus">Hidden testability hook: emit <c>phase=name status=skipped reason=...</c> lines on stderr.</param>
public sealed record BootstrapOptions(
    BootstrapCommand Command,
    string? ConfigPath,
    string OutputDir,
    bool SkipPrereqs,
    bool RotateSecrets,
    bool RotateCerts,
    bool NonInteractive,
    string? FaultInject,
    bool PrintPhaseStatus);

public enum BootstrapCommand
{
    Bootstrap,
    Publish,
    Up,
    RotateSecrets,
    RotateCerts,
}
