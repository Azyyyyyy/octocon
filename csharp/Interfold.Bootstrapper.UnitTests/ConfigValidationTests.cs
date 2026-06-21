using System.Diagnostics;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigPhase.Validate"/>. Each test stages a single invariant violation
/// against an otherwise-valid <see cref="BootstrapConfig"/> and asserts the validator throws with a
/// message naming the offending field — the human-readable error text matters because the operator
/// sees it inline when bootstrap fails.
/// </summary>
public sealed class ConfigValidationTests
{
    /// <summary>Default-construct a config that already satisfies every invariant.</summary>
    private static BootstrapConfig MakeValid() => new()
    {
        Deployment =
        {
            OutputDir = "./deploy",
            Domains = ["api.example.com"],
            RootCaName = "Interfold Root CA",
            CertYears = 5,
            TrustStoreInstall = true,
        },
        Ports =
        {
            ApiHttp = 5000,
            ApiHttps = 5001,
            WebHttp = 8080,
            WebHttps = 8081,
        },
        DatabaseMode = "single",
    };

    [Test]
    public async Task ValidConfigPasses()
    {
        // Sanity check: the helper itself must satisfy the validator, otherwise every other test
        // here is testing the wrong thing.
        ConfigPhase.Validate(MakeValid());
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyDomainsListFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.Domains = [];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("domains");
    }

    [Test]
    public async Task ZeroCertYearsFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.CertYears = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("certYears");
    }

    [Test]
    public async Task NegativeCertYearsFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.CertYears = -1;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("certYears");
    }

    [Test]
    public async Task CertYearsOverThirtyFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.CertYears = 31;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("certYears");
    }

    [Test]
    public async Task ApiHttpEqualsApiHttpsFailsValidation()
    {
        var cfg = MakeValid();
        // Same host port can't bind two listeners; the validator must surface this before publish.
        cfg.Ports.ApiHttp = 5000;
        cfg.Ports.ApiHttps = 5000;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("apiHttp");
        await Assert.That(ex.Message).Contains("apiHttps");
    }

    [Test]
    public async Task DomainWithSpaceFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.Domains = ["api example.com"];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("Invalid domain");
    }

    [Test]
    public async Task PortAboveMaxFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Ports.ApiHttp = 70000;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("ApiHttp");
    }

    [Test]
    public async Task InvalidDatabaseModeFailsValidation()
    {
        var cfg = MakeValid();
        cfg.DatabaseMode = "quadruple-redundant";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("databaseMode");
    }

    [Test]
    public async Task DefaultPostgresDatabasePasses()
    {
        // The shipping default value must always pass validation - any drift here would brick
        // every operator who relies on the out-of-the-box configuration.
        var cfg = MakeValid();
        cfg.PostgresDatabase = "interfold";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomSafePostgresDatabasePasses()
    {
        // Any identifier matching ^[A-Za-z_][A-Za-z0-9_]{0,62}$ must round-trip cleanly. We
        // pick a value that exercises underscores + digits because operators on shared clusters
        // commonly suffix the database name with an env tag (e.g. acme_prod).
        var cfg = MakeValid();
        cfg.PostgresDatabase = "acme_prod_42";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyPostgresDatabaseFailsValidation()
    {
        var cfg = MakeValid();
        cfg.PostgresDatabase = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task WhitespacePostgresDatabaseFailsValidation()
    {
        var cfg = MakeValid();
        cfg.PostgresDatabase = "   ";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task PostgresDatabaseStartingWithDigitFailsValidation()
    {
        // Postgres tolerates a leading digit only inside double quotes; rejecting it up front
        // keeps the seeder's CREATE DATABASE "<name>" emission unsurprising and avoids
        // identifier-handling drift between quoted and unquoted call sites.
        var cfg = MakeValid();
        cfg.PostgresDatabase = "1interfold";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task PostgresDatabaseWithDashFailsValidation()
    {
        // A dash is a valid character inside double-quoted Postgres identifiers but is forbidden
        // by our pattern so the value also works as a default role / schema prefix downstream
        // without further quoting gymnastics.
        var cfg = MakeValid();
        cfg.PostgresDatabase = "inter-fold";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task DefaultClusterNamePasses()
    {
        // The shipping default must always pass validation, exactly like postgresDatabase.
        var cfg = MakeValid();
        cfg.ClusterName = "InterfoldCluster";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomClusterNameWithSpacesPasses()
    {
        // Cluster names are advertised in gossip metadata and shown in DESCRIBE CLUSTER output;
        // operators routinely put a human-readable name with spaces here (e.g. "Acme Prod"),
        // so we must accept spaces (unlike postgresDatabase).
        var cfg = MakeValid();
        cfg.ClusterName = "Acme Prod 1.0";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyClusterNameFailsValidation()
    {
        var cfg = MakeValid();
        cfg.ClusterName = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task ClusterNameWithSingleQuoteFailsValidation()
    {
        // Single quotes are the highest-risk character because Cassandra's entrypoint rewrites
        // cassandra.yaml with the value pasted in; an unescaped quote would corrupt the YAML.
        var cfg = MakeValid();
        cfg.ClusterName = "Acme'Prod";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task ClusterNameWithNewlineFailsValidation()
    {
        var cfg = MakeValid();
        cfg.ClusterName = "Acme\nProd";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task OverlyLongClusterNameFailsValidation()
    {
        // 64 chars is the published Cassandra limit; anything past it must fail.
        var cfg = MakeValid();
        cfg.ClusterName = new string('A', 65);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task MalformedJsonReturnsClearError()
    {
        // ConfigPhase.RunAsync uses JsonSerializer.Deserialize, which surfaces a JsonException
        // when the file is malformed; we exercise that path here so future regressions in error
        // handling are caught at unit speed instead of via a full DinD spin-up. We drive the
        // `publish` command (not `bootstrap`) so the prereqs phase is skipped — this keeps the
        // test runnable on Windows / macOS as well as Linux.
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-cfg-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
            await File.WriteAllTextAsync(configPath, "{ this is not valid json");

            // Shell out to the binary so we exercise the real exit-code path the operator sees.
            var result = await RunBootstrapperAsync("publish", "--config", configPath,
                "--output-dir", tmpDir, "--non-interactive");

            await Assert.That(result.ExitCode).IsNotEqualTo(0)
                .Because("malformed JSON must abort the bootstrap");
            // The thrown JsonException's message varies across SDK versions but always names the
            // JSON parse failure mode in some form.
            var combined = result.Stdout + result.Stderr;
            await Assert.That(combined).Contains("JSON")
                .Or.Contains("json")
                .Or.Contains("parse");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Test]
    public async Task MissingFileWithNonInteractiveExitsWithMessage()
    {
        // Drives `publish` (not `bootstrap`) so the prereqs phase is bypassed on non-Linux hosts.
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-cfg-missing-ni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
            await Assert.That(File.Exists(configPath)).IsFalse();

            var result = await RunBootstrapperAsync("publish", "--config", configPath,
                "--output-dir", tmpDir, "--non-interactive");

            await Assert.That(result.ExitCode).IsNotEqualTo(0);
            await Assert.That(result.Stdout + result.Stderr)
                .Contains("Config file not found")
                .Or.Contains("non-interactive");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Shell out to the compiled bootstrapper so we exercise the exact same dispatch path the
    /// operator hits. We rely on TestSupport.BootstrapperPath to locate the Debug build; if it's
    /// absent the test is skipped (TUnit will surface this on the first call).
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBootstrapperAsync(params string[] args)
    {
        var path = TestSupport.BootstrapperBinaryOrSkip();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Strip TTY so the "interactive" branch never triggers on hosts that happen to have one.
            // The --non-interactive flag also covers this in the bootstrapper, but redirecting
            // stdin is what *really* eliminates the prompt path.
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet");
        proc.StandardInput.Close();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }
}
