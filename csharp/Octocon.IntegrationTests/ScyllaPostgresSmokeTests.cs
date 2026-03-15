using System.Diagnostics;
using TUnit.Core;

namespace Octocon.IntegrationTests;

public sealed class ScyllaPostgresSmokeTests
{
    [Test]
    public async Task AlterCreate_IdempotentReplay_WorksAgainstLiveAdapters()
    {
        if (!ShouldRunLiveIntegration())
        {
            return;
        }

        var workspaceRoot = FindWorkspaceRoot();
        var projectPath = Path.Combine(workspaceRoot, "csharp", "Octocon.Cli", "Octocon.Cli.csproj");

        var systemId = $"itest-{Guid.NewGuid():N}"[..14];
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var baseArgs =
            $"run --project \"{projectPath}\" -- " +
            "--persistence=scylla-postgres " +
            $"--scylla-contact-points={GetEnv("OCTOCON_TEST_SCYLLA_CONTACT_POINTS", "127.0.0.1")} " +
            $"--scylla-username={GetEnv("OCTOCON_TEST_SCYLLA_USERNAME", "cassandra")} " +
            $"--scylla-password={GetEnv("OCTOCON_TEST_SCYLLA_PASSWORD", "cassandra")} " +
            $"--region={GetEnv("OCTOCON_TEST_REGION", "nam")} " +
            "alter-create " +
            $"--system {systemId} " +
            "--name IntegrationSmoke " +
            $"--idempotency-key {idempotencyKey}";

        var first = await RunDotnetAsync(baseArgs, workspaceRoot);
        Ensure(first.ExitCode == 0, $"First CLI invocation failed. stderr: {first.StdErr}");
        Ensure(first.StdOut.Contains("accepted", StringComparison.OrdinalIgnoreCase),
            $"First CLI invocation did not contain accepted. stdout: {first.StdOut}");
        Ensure(first.StdOut.Contains("\"Replay\":false", StringComparison.Ordinal),
            $"First CLI invocation did not contain replay=false. stdout: {first.StdOut}");

        var second = await RunDotnetAsync(baseArgs, workspaceRoot);
        Ensure(second.ExitCode == 0, $"Second CLI invocation failed. stderr: {second.StdErr}");
        Ensure(second.StdOut.Contains("accepted", StringComparison.OrdinalIgnoreCase),
            $"Second CLI invocation did not contain accepted. stdout: {second.StdOut}");
        Ensure(second.StdOut.Contains("\"Replay\":true", StringComparison.Ordinal),
            $"Second CLI invocation did not contain replay=true. stdout: {second.StdOut}");
    }

    private static bool ShouldRunLiveIntegration()
    {
        var run = Environment.GetEnvironmentVariable("OCTOCON_RUN_LIVE_INTEGRATION");
        if (!bool.TryParse(run, out var enabled) || !enabled)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OCTOCON_POSTGRES_CONNECTION"));
    }

    private static string GetEnv(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "octocon.sln");
            if (File.Exists(marker))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find workspace root containing octocon.sln.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDotnetAsync(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.Environment["OCTOCON_POSTGRES_CONNECTION"] = Environment.GetEnvironmentVariable("OCTOCON_POSTGRES_CONNECTION") ?? string.Empty;
        psi.Environment["OCTOCON_PERSISTENCE"] = "scylla-postgres";

        using var process = new Process { StartInfo = psi };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
