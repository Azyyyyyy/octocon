using System.CommandLine;
using System.CommandLine.Parsing;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// Entry point and command tree for the bootstrapper CLI.
/// Commands: <c>bootstrap</c> (default), <c>publish</c>, <c>up</c>, <c>rotate-secrets</c>, <c>rotate-certs</c>.
/// </summary>
public static class RootCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = BuildRoot();
        var parseResult = root.Parse(args);
        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }

    private static RootCommand BuildRoot()
    {
        var configOpt = new Option<string?>("--config", "-c")
        {
            Description = "Path to interfold.bootstrap.json. If omitted, the bootstrapper prompts interactively (tty only)."
        };
        var outputDirOpt = new Option<string>("--output-dir", "-o")
        {
            Description = "Root directory for emitted artifacts (compose, certs, secrets).",
            DefaultValueFactory = _ => "./deploy"
        };
        var skipPrereqsOpt = new Option<bool>("--skip-prereqs")
        {
            Description = "Skip the prerequisites phase (Docker/openssl install, AIO sysctl). Use on re-runs."
        };
        var nonInteractiveOpt = new Option<bool>("--non-interactive")
        {
            Description = "Fail rather than prompt when config values are missing."
        };
        // Hidden testability flags - not surfaced in help but accepted by the parser.
        var faultInjectOpt = new Option<string?>("--fault-inject") { Hidden = true };
        var printPhaseStatusOpt = new Option<bool>("--print-phase-status") { Hidden = true };

        var root = new RootCommand(
            "Interfold self-hosting bootstrapper. Brings a fresh Linux box from 'git clone' to a running stack.");

        // ---------- bootstrap (default) ----------
        var bootstrapCmd = new Command("bootstrap",
            "Run all phases: prereqs -> config -> secrets -> certs -> publish -> launch.");
        AddSharedOptions(bootstrapCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        bootstrapCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Bootstrap, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(bootstrapCmd);

        // ---------- publish (compose-only, no docker compose up) ----------
        var publishCmd = new Command("publish",
            "Run config + secrets + certs + compose-publish without launching the stack.");
        AddSharedOptions(publishCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        publishCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Publish, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(publishCmd);

        // ---------- up (launch only against existing compose) ----------
        var upCmd = new Command("up",
            "Launch an already-generated compose stack: docker compose up -d + health wait.");
        AddSharedOptions(upCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        upCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Up, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(upCmd);

        // ---------- rotate-secrets ----------
        var rotateSecretsCmd = new Command("rotate-secrets",
            "Regenerate DB/admin passwords, encryption keypair, and pepper. Re-emits compose + restarts API.");
        AddSharedOptions(rotateSecretsCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        rotateSecretsCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.RotateSecrets, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: true, rotateCerts: false, ct));
        root.Subcommands.Add(rotateSecretsCmd);

        // ---------- rotate-certs ----------
        var rotateCertsCmd = new Command("rotate-certs",
            "Regenerate root CA + leaf cert. Re-installs into the system trust store.");
        AddSharedOptions(rotateCertsCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        rotateCertsCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.RotateCerts, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: true, ct));
        root.Subcommands.Add(rotateCertsCmd);

        // ---------- show-trust ----------
        var showTrustCmd = new Command("show-trust",
            "Print the root CA path, SHA-256 fingerprint, expiry, and fetch/verify snippet. Read-only.");
        AddSharedOptions(showTrustCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        showTrustCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.ShowTrust, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(showTrustCmd);

        // No subcommand -> default to `bootstrap`.
        AddSharedOptions(root, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        root.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Bootstrap, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));

        return root;
    }

    private static void AddSharedOptions(
        Command target,
        Option<string?> configOpt,
        Option<string> outputDirOpt,
        Option<bool> skipPrereqsOpt,
        Option<bool> nonInteractiveOpt,
        Option<string?> faultInjectOpt,
        Option<bool> printPhaseStatusOpt)
    {
        target.Options.Add(configOpt);
        target.Options.Add(outputDirOpt);
        target.Options.Add(skipPrereqsOpt);
        target.Options.Add(nonInteractiveOpt);
        target.Options.Add(faultInjectOpt);
        target.Options.Add(printPhaseStatusOpt);
    }

    private static async Task<int> InvokeAsync(
        BootstrapCommand command,
        ParseResult parse,
        Option<string?> configOpt,
        Option<string> outputDirOpt,
        Option<bool> skipPrereqsOpt,
        Option<bool> nonInteractiveOpt,
        Option<string?> faultInjectOpt,
        Option<bool> printPhaseStatusOpt,
        bool rotateSecrets,
        bool rotateCerts,
        CancellationToken ct)
    {
        var options = new BootstrapOptions(
            Command: command,
            ConfigPath: parse.GetValue(configOpt),
            OutputDir: Path.GetFullPath(parse.GetValue(outputDirOpt) ?? "./deploy"),
            SkipPrereqs: parse.GetValue(skipPrereqsOpt),
            RotateSecrets: rotateSecrets,
            RotateCerts: rotateCerts,
            NonInteractive: parse.GetValue(nonInteractiveOpt),
            FaultInject: parse.GetValue(faultInjectOpt),
            PrintPhaseStatus: parse.GetValue(printPhaseStatusOpt));

        var logger = new PhaseLogger(options);

        try
        {
            return await Orchestrator.RunAsync(options, logger, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.Error("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
            return 1;
        }
    }
}
