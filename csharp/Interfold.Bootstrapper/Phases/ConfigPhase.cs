using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 2 — loads <c>interfold.bootstrap.json</c> from disk or builds it via interactive prompts,
/// then validates and persists the resulting <see cref="BootstrapConfig"/>.
/// </summary>
internal static class ConfigPhase
{
    public static async Task<BootstrapConfig> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        const string Phase = "config";
        logger.PhaseStart(Phase);

        var configPath = options.ConfigPath ?? Path.Combine(options.OutputDir, "interfold.bootstrap.json");
        BootstrapConfig config;

        if (File.Exists(configPath))
        {
            logger.Info($"    loading config from {configPath}");
            var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            config = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig)
                     ?? throw new InvalidOperationException($"Failed to parse {configPath} (returned null).");
        }
        else if (options.NonInteractive)
        {
            logger.PhaseFail(Phase, "missing-config-non-interactive");
            throw new InvalidOperationException(
                $"Config file not found at {configPath} and --non-interactive was set. " +
                "Provide --config <path> or rerun interactively.");
        }
        else if (!Console.IsInputRedirected)
        {
            logger.Info($"    no config at {configPath}; entering interactive setup");
            config = PromptForConfig(Console.In, Console.Out);
            await PersistAsync(config, configPath, ct).ConfigureAwait(false);
        }
        else
        {
            logger.PhaseFail(Phase, "missing-config-no-tty");
            throw new InvalidOperationException(
                $"Config file not found at {configPath} and no TTY is available. " +
                "Run with --config <path> pointing at a populated interfold.bootstrap.json.");
        }

        Validate(config);

        // Align the config's outputDir with the CLI override (if the operator passed --output-dir).
        if (!string.Equals(Path.GetFullPath(config.Deployment.OutputDir), options.OutputDir, StringComparison.Ordinal))
        {
            logger.Info($"    overriding config.outputDir with --output-dir={options.OutputDir}");
            config.Deployment.OutputDir = options.OutputDir;
        }

        logger.PhaseDone(Phase);
        return config;
    }

    /// <summary>
    /// Interactive setup prompt. Reads from <paramref name="reader"/> and writes prompts to
    /// <paramref name="writer"/> so unit tests can drive the flow with <see cref="StringReader"/>
    /// / <see cref="StringWriter"/>. Production callers pass <see cref="Console.In"/> and
    /// <see cref="Console.Out"/>; the IO seam exists only to make the prompt logic testable.
    /// </summary>
    internal static BootstrapConfig PromptForConfig(TextReader reader, TextWriter writer)
    {
        var config = new BootstrapConfig();

        writer.Write("Output directory [./deploy]: ");
        var output = reader.ReadLine();
        if (!string.IsNullOrWhiteSpace(output))
        {
            config.Deployment.OutputDir = output.Trim();
        }

        writer.Write("Public domain(s), comma separated (e.g. api.example.com): ");
        var domainInput = reader.ReadLine();
        if (!string.IsNullOrWhiteSpace(domainInput))
        {
            config.Deployment.Domains = domainInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        writer.Write("Root CA subject [Interfold Root CA]: ");
        var rootCa = reader.ReadLine();
        if (!string.IsNullOrWhiteSpace(rootCa))
        {
            config.Deployment.RootCaName = rootCa.Trim();
        }

        writer.Write("Database mode (single|multi|cassandra) [single]: ");
        var mode = reader.ReadLine();
        if (!string.IsNullOrWhiteSpace(mode))
        {
            config.DatabaseMode = mode.Trim();
        }

        writer.Write("Google OAuth client secret (blank to skip): ");
        config.OAuth.GoogleClientSecret = reader.ReadLine()?.Trim() ?? string.Empty;

        writer.Write("Discord OAuth client secret (blank to skip): ");
        config.OAuth.DiscordClientSecret = reader.ReadLine()?.Trim() ?? string.Empty;

        return config;
    }

    /// <summary>
    /// Validates a loaded or freshly-prompted config. Internal so the unit-test project can
    /// drive validation failures directly without round-tripping through file IO + RunAsync.
    /// Throws <see cref="InvalidOperationException"/> with an operator-readable message on the
    /// first invariant that's broken.
    /// </summary>
    internal static void Validate(BootstrapConfig config)
    {
        if (config.Deployment.Domains.Count == 0)
        {
            throw new InvalidOperationException("config.deployment.domains must contain at least one domain.");
        }

        foreach (var domain in config.Deployment.Domains)
        {
            // Lightweight RFC 1035 check - rejects obvious typos without pulling in a DNS library.
            if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || domain.Contains(' '))
            {
                throw new InvalidOperationException($"Invalid domain '{domain}'.");
            }
        }

        if (config.Deployment.CertYears is < 1 or > 30)
        {
            throw new InvalidOperationException(
                $"config.deployment.certYears={config.Deployment.CertYears} is outside the allowed 1..30 range.");
        }

        ValidatePort(config.Ports.ApiHttp, nameof(config.Ports.ApiHttp));
        ValidatePort(config.Ports.ApiHttps, nameof(config.Ports.ApiHttps));
        ValidatePort(config.Ports.WebHttp, nameof(config.Ports.WebHttp));
        ValidatePort(config.Ports.WebHttps, nameof(config.Ports.WebHttps));

        // Same host port can only be bound once per compose project, so plain-HTTP and HTTPS
        // listeners cannot share it. We only check the API pair here because the API container is
        // the only one whose plain-HTTP + HTTPS endpoints are both bound by default; the web
        // listeners share their port-pair handling at the AppHost layer.
        if (config.Ports.ApiHttp == config.Ports.ApiHttps)
        {
            throw new InvalidOperationException(
                $"config.ports.apiHttp ({config.Ports.ApiHttp}) and config.ports.apiHttps must be different ports.");
        }

        if (config.DatabaseMode is not ("single" or "multi" or "cassandra"))
        {
            throw new InvalidOperationException(
                $"config.databaseMode='{config.DatabaseMode}' is invalid. Expected: single | multi | cassandra. " +
                "(Translates to AppHost parameters include-scylla / include-cassandra / scylla-topology.)");
        }
    }

    private static void ValidatePort(int port, string field)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{field}={port} is outside the valid 1..65535 range.");
        }
    }

    private static async Task PersistAsync(BootstrapConfig config, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, BootstrapJsonContext.Default.BootstrapConfig);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }
}
