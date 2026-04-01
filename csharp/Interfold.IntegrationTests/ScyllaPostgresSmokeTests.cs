using System.Diagnostics;

namespace Interfold.IntegrationTests;

public sealed class ScyllaPostgresSmokeTests
{

    [Test]
    public async Task AlterCreate_IdempotentReplay_WorksAgainstLiveAdapters()
    {
        if (!(IntegrationTestEnvironment.ShouldRunLiveIntegration && IntegrationTestEnvironment.HasPostgresConnection))
        {
            return;
        }

        var workspaceRoot = FindWorkspaceRoot();
        var projectPath = Path.Combine(workspaceRoot, "csharp", "Interfold.Cli", "Interfold.Cli.csproj");

        var systemId = $"itest-{Guid.NewGuid():N}"[..14];
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var baseArgs =
            $"run --project \"{projectPath}\" -- " +
            "--persistence=scylla-postgres " +
            $"--scylla-contact-points={IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_CONTACT_POINTS", "127.0.0.1")} " +
            $"--scylla-username={IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_USERNAME", "cassandra")} " +
            $"--scylla-password={IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_PASSWORD", "cassandra")} " +
            $"--region={IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_REGION", "nam")} " +
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

        psi.Environment["OCTOCON_POSTGRES_CONNECTION"] = IntegrationTestEnvironment.PostgresConnection ?? string.Empty;
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
