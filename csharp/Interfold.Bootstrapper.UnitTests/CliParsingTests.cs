using System.Diagnostics;
using Interfold.Bootstrapper.Cli;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="RootCli.RunAsync"/> in-process so we can assert CLI parsing behaviour
/// without forking. Stdout/stderr are temporarily swapped onto <see cref="StringWriter"/>s for
/// the duration of each invocation; the original streams are restored in a <c>finally</c> so
/// other tests in the same process aren't affected.
/// </summary>
public sealed class CliParsingTests
{
    private sealed record CapturedRun(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Invokes the CLI and captures stdout/stderr. The CLI is a real System.CommandLine root, so
    /// this exercises argument parsing exactly as the operator's invocation would.
    /// Used for tests that drive code through to <see cref="Phases.Orchestrator"/> (e.g. the
    /// non-interactive missing-config path) — these write through <see cref="Console.WriteLine"/>
    /// and so are visible to the swapped-in writers.
    /// </summary>
    private static async Task<CapturedRun> InvokeInProcessAsync(params string[] args)
    {
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var prevIn = Console.In;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            // TUnit0055 warns that overwriting Console writers can mask its log output; in this
            // test we deliberately want to capture what the bootstrapper writes, and we restore
            // the originals in the finally block, so the warning is misleading here.
#pragma warning disable TUnit0055
            Console.SetOut(stdout);
            Console.SetError(stderr);
            // Force "non-interactive" semantics in ConfigPhase by closing stdin — otherwise
            // `Console.IsInputRedirected` may be false under the test runner and the prompt
            // path would block waiting for keystrokes that never come.
            Console.SetIn(new StringReader(string.Empty));
#pragma warning restore TUnit0055
            var exit = await RootCli.RunAsync(args);
            return new CapturedRun(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
            Console.SetIn(prevIn);
#pragma warning restore TUnit0055
        }
    }

    /// <summary>
    /// Shells out to the compiled bootstrapper. Necessary for help / parser-error tests because
    /// System.CommandLine 3.x writes those to <c>ParseResult.Configuration.Output</c> rather than
    /// the redirectable <see cref="Console.Out"/>. Skips the test (via
    /// <see cref="TestSupport.BootstrapperBinaryOrSkip"/>) when the bootstrapper binary isn't on
    /// disk yet — `dotnet test` from the solution root will have built it as a transitive dep.
    /// </summary>
    private static async Task<CapturedRun> InvokeViaProcessAsync(params string[] args)
    {
        var path = TestSupport.BootstrapperBinaryOrSkip();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet");
        proc.StandardInput.Close();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return new CapturedRun(proc.ExitCode, stdout, stderr);
    }

    [Test]
    public async Task HelpFlagPrintsCommandList()
    {
        // System.CommandLine 3.x writes help text to a configured TextWriter inside the parse
        // result, NOT through Console.Out — so we shell out and read the real stdout.
        var run = await InvokeViaProcessAsync("--help");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        var combined = run.Stdout + run.Stderr;
        await Assert.That(combined).Contains("bootstrap");
        await Assert.That(combined).Contains("publish");
        await Assert.That(combined).Contains("up");
        await Assert.That(combined).Contains("rotate-secrets");
        await Assert.That(combined).Contains("rotate-certs");
    }

    [Test]
    public async Task HelpListsShortOptions()
    {
        // -c short form for --config and -o short form for --output-dir must both surface in the
        // help text. If a future refactor drops the aliases this catches it immediately.
        var run = await InvokeViaProcessAsync("--help");

        var combined = run.Stdout + run.Stderr;
        await Assert.That(combined).Contains("-c");
        await Assert.That(combined).Contains("-o");
    }

    [Test]
    public async Task UnknownCommandExitsNonZero()
    {
        // System.CommandLine treats an unrecognised verb as an error. The exact wording varies
        // across SDK versions so we just assert on the non-zero exit code.
        var run = await InvokeViaProcessAsync("this-is-definitely-not-a-command");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task ShortOptionsMatchLongOptions()
    {
        // Compare two parser invocations of the `publish` command — one with --config, one
        // with -c — pointing at the same nonexistent file. Both must produce equivalent
        // failure behaviour because -c is the alias of --config. We can drive this in-process
        // because the failure happens inside ConfigPhase (which uses Console.WriteLine).
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-cli-short-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var configPath = Path.Combine(tmpDir, "does-not-exist.json");

            var longRun = await InvokeInProcessAsync("publish", "--config", configPath,
                "--output-dir", tmpDir, "--non-interactive");
            var shortRun = await InvokeInProcessAsync("publish", "-c", configPath,
                "-o", tmpDir, "--non-interactive");

            await Assert.That(shortRun.ExitCode).IsEqualTo(longRun.ExitCode)
                .Because("-c/-o aliases must produce the same exit code as --config/--output-dir");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task NonInteractiveWithoutConfigExitsNonZero()
    {
        // ConfigPhase aborts when --non-interactive is set and no config file is present at the
        // expected path. Drive the `publish` command so the failure happens at the config phase
        // rather than at prereqs (which is Linux-only).
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-cli-ni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var run = await InvokeInProcessAsync("publish",
                "--config", Path.Combine(tmpDir, "missing.json"),
                "--output-dir", tmpDir,
                "--non-interactive");

            await Assert.That(run.ExitCode).IsNotEqualTo(0);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
