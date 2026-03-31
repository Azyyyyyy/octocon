using Microsoft.Extensions.DependencyInjection;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Fronting;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.Persistence;
using Interfold.Infrastructure.Persistence.Bootstrap;

if (args.Length == 0 || IsHelp(args[0]))
{
    PrintHelp();
    return 0;
}

var (command, options) = ParseInput(args);

if (string.IsNullOrWhiteSpace(command))
{
    PrintHelp();
    return 0;
}

var runtime = BuildRuntime(options);

try
{
    return command switch
    {
        "bootstrap-check" => await HandleBootstrapCheck(runtime),
        "account-username-update" => await HandleAccountUsernameUpdate(runtime, options),
        "alter-create" => await HandleAlterCreate(runtime, options),
        "alter-update" => await HandleAlterUpdate(runtime, options),
        "front-start" => await HandleFrontStart(runtime, options),
        "front-end" => await HandleFrontEnd(runtime, options),
        "front-primary" => await HandleFrontPrimary(runtime, options),
        _ => UnknownCommand(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

static (string? Command, IReadOnlyDictionary<string, string> Options) ParseInput(string[] raw)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? command = null;

    for (var i = 0; i < raw.Length; i++)
    {
        var arg = raw[i];

        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            var option = arg[2..];
            var separatorIndex = option.IndexOf('=');

            string key;
            string value;

            if (separatorIndex >= 0)
            {
                key = option[..separatorIndex];
                value = option[(separatorIndex + 1)..];
            }
            else
            {
                key = option;
                value = (i + 1 < raw.Length && !raw[i + 1].StartsWith("--", StringComparison.Ordinal))
                    ? raw[++i]
                    : "true";
            }

            options[key] = value;
            continue;
        }

        command ??= arg.ToLowerInvariant();
    }

    return (command, options);
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 2;
}

static async Task<int> HandleAccountUsernameUpdate(CliRuntime runtime, IReadOnlyDictionary<string, string> options)
{
    var systemId = Require(options, "system");
    var username = Require(options, "username");

    var envelope = new CommandEnvelope<UpdateUsernameCommand>(
        OperationIds.AccountUsernameUpdate,
        Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: GetOrCreateIdempotency(options),
        ExpectedVersion: TryLong(options, "expected-version"),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new UpdateUsernameCommand(username)
    );

    var result = await runtime.UpdateUsernameHandler.HandleAsync(envelope);
    return PrintResult(result);
}

static async Task<int> HandleAlterCreate(CliRuntime runtime, IReadOnlyDictionary<string, string> options)
{
    var systemId = Require(options, "system");
    var name = Require(options, "name");

    var envelope = new CommandEnvelope<CreateAlterCommand>(
        OperationIds.AlterCreate,
        Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: GetOrCreateIdempotency(options),
        ExpectedVersion: TryLong(options, "expected-version"),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new CreateAlterCommand(name)
    );

    var result = await runtime.CreateAlterHandler.HandleAsync(envelope);
    return PrintResult(result);
}

static async Task<int> HandleAlterUpdate(CliRuntime runtime, IReadOnlyDictionary<string, string> options)
{
    var systemId = Require(options, "system");
    var alterId = int.Parse(Require(options, "alter-id"));

    var payload = new UpdateAlterCommand(
        AlterId: alterId,
        Name: TryGet(options, "name"),
        Description: TryGet(options, "description"),
        AvatarUrl: TryGet(options, "avatar-url"),
        Color: TryGet(options, "color"),
        Pronouns: TryGet(options, "pronouns"),
        SecurityLevel: TryGet(options, "security-level"),
        Fields: null,
        ProxyName: TryGet(options, "proxy-name"),
        Alias: TryGet(options, "alias"),
        Untracked: TryBool(options, "untracked"),
        Archived: TryBool(options, "archived"),
        Pinned: TryBool(options, "pinned")
    );

    var envelope = new CommandEnvelope<UpdateAlterCommand>(
        OperationIds.AlterUpdate,
        Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: GetOrCreateIdempotency(options),
        ExpectedVersion: TryLong(options, "expected-version"),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: payload
    );

    var result = await runtime.UpdateAlterHandler.HandleAsync(envelope);
    return PrintResult(result);
}

static async Task<int> HandleFrontStart(CliRuntime runtime, IReadOnlyDictionary<string, string> options)
{
    var systemId = Require(options, "system");
    var alterId = int.Parse(Require(options, "alter-id"));

    var envelope = new CommandEnvelope<StartFrontCommand>(
        OperationIds.FrontStart,
        Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: GetOrCreateIdempotency(options),
        ExpectedVersion: TryLong(options, "expected-version"),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new StartFrontCommand(alterId, TryGet(options, "comment"))
    );

    var result = await runtime.StartFrontHandler.HandleAsync(envelope);
    return PrintResult(result);
}

static async Task<int> HandleFrontEnd(CliRuntime runtime, IReadOnlyDictionary<string, string> options)
{
    var systemId = Require(options, "system");
    var alterId = int.Parse(Require(options, "alter-id"));

    var envelope = new CommandEnvelope<EndFrontCommand>(
        OperationIds.FrontEnd,
        Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: GetOrCreateIdempotency(options),
        ExpectedVersion: TryLong(options, "expected-version"),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new EndFrontCommand(alterId)
    );

    var result = await runtime.EndFrontHandler.HandleAsync(envelope);
    return PrintResult(result);
}

static async Task<int> HandleFrontPrimary(CliRuntime runtime, IReadOnlyDictionary<string, string> options)
{
    var systemId = Require(options, "system");
    var alterId = TryInt(options, "alter-id");

    var envelope = new CommandEnvelope<SetPrimaryFrontCommand>(
        OperationIds.FrontPrimary,
        Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: GetOrCreateIdempotency(options),
        ExpectedVersion: TryLong(options, "expected-version"),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new SetPrimaryFrontCommand(alterId)
    );

    var result = await runtime.SetPrimaryFrontHandler.HandleAsync(envelope);
    return PrintResult(result);
}

static async Task<int> HandleBootstrapCheck(CliRuntime runtime)
{
    var result = await runtime.BootstrapHealthChecker.CheckAsync();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    return result.Healthy ? 0 : 4;
}

static int PrintResult<TResult>(CommandExecutionResult<TResult> result)
{
    if (result.Accepted)
    {
        Console.WriteLine("accepted");
        if (result.Result is not null)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Result));
        }

        return 0;
    }

    Console.WriteLine("rejected");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Conflict));
    return 3;
}

static CliRuntime BuildRuntime(IReadOnlyDictionary<string, string> options)
{
    var persistence = ResolvePersistenceMode(options);
    var nodeGroup = NodeGroupResolver.Resolve();
    var services = new ServiceCollection();

    services.AddInterfoldPersistence(persistence, cfg =>
    {
        cfg.DefaultRegion =
            TryGet(options, "region") ??
            Environment.GetEnvironmentVariable("OCTOCON_REGION") ??
            cfg.DefaultRegion;

        cfg.PostgresConnectionString =
            TryGet(options, "postgres-connection") ??
            Environment.GetEnvironmentVariable("OCTOCON_POSTGRES_CONNECTION") ??
            cfg.PostgresConnectionString;

        cfg.ScyllaKeyspace =
            TryGet(options, "scylla-keyspace") ??
            Environment.GetEnvironmentVariable("OCTOCON_SCYLLA_KEYSPACE") ??
            cfg.DefaultRegion;

        cfg.ScyllaLocalDatacenter =
            TryGet(options, "scylla-datacenter") ??
            Environment.GetEnvironmentVariable("OCTOCON_SCYLLA_DATACENTER") ??
            cfg.ScyllaLocalDatacenter;

        var contactPoints =
            TryGet(options, "scylla-contact-points") ??
            Environment.GetEnvironmentVariable("OCTOCON_SCYLLA_CONTACT_POINTS");

        if (!string.IsNullOrWhiteSpace(contactPoints))
        {
            cfg.ScyllaContactPoints = contactPoints
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        cfg.ScyllaUsername =
            TryGet(options, "scylla-username") ??
            Environment.GetEnvironmentVariable("OCTOCON_SCYLLA_USERNAME");

        cfg.ScyllaPassword =
            TryGet(options, "scylla-password") ??
            Environment.GetEnvironmentVariable("OCTOCON_SCYLLA_PASSWORD");

        cfg.DbRetryAttempts =
            TryInt(options, "db-retry-attempts") ??
            TryIntFromEnv("OCTOCON_DB_RETRY_ATTEMPTS") ??
            cfg.DbRetryAttempts;

        cfg.DbRetryInitialDelayMs =
            TryInt(options, "db-retry-initial-delay-ms") ??
            TryIntFromEnv("OCTOCON_DB_RETRY_INITIAL_DELAY_MS") ??
            cfg.DbRetryInitialDelayMs;

        cfg.DbRetryMaxDelayMs =
            TryInt(options, "db-retry-max-delay-ms") ??
            TryIntFromEnv("OCTOCON_DB_RETRY_MAX_DELAY_MS") ??
            cfg.DbRetryMaxDelayMs;
    });
    services.AddInterfoldCluster(nodeGroup);
    services.AddSingleton<UpdateUsernameCommandHandler>();
    services.AddSingleton<CreateAlterCommandHandler>();
    services.AddSingleton<UpdateAlterCommandHandler>();
    services.AddSingleton<StartFrontCommandHandler>();
    services.AddSingleton<EndFrontCommandHandler>();
    services.AddSingleton<SetPrimaryFrontCommandHandler>();
    services.AddSingleton<CliRuntime>();

    var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<CliRuntime>();
}

static PersistenceMode ResolvePersistenceMode(IReadOnlyDictionary<string, string> options)
{
    var configured = TryGet(options, "persistence")
        ?? Environment.GetEnvironmentVariable("OCTOCON_PERSISTENCE")
        ?? "scylla-postgres";

    return configured.ToLowerInvariant() switch
    {
        "inmemory" => PersistenceMode.InMemory,
        "scylla-postgres" => PersistenceMode.ScyllaPostgres,
        _ => throw new InvalidOperationException(
            $"Unsupported persistence mode '{configured}'. Use 'inmemory' or 'scylla-postgres'."
        )
    };
}

static string Require(IReadOnlyDictionary<string, string> options, string key)
{
    if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required option: --{key}");
    }

    return value;
}

static string? TryGet(IReadOnlyDictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? value : null;

static long? TryLong(IReadOnlyDictionary<string, string> options, string key)
{
    if (!options.TryGetValue(key, out var value))
    {
        return null;
    }

    return long.TryParse(value, out var parsed) ? parsed : null;
}

static int? TryInt(IReadOnlyDictionary<string, string> options, string key)
{
    if (!options.TryGetValue(key, out var value))
    {
        return null;
    }

    return int.TryParse(value, out var parsed) ? parsed : null;
}

static bool? TryBool(IReadOnlyDictionary<string, string> options, string key)
{
    if (!options.TryGetValue(key, out var value))
    {
        return null;
    }

    return bool.TryParse(value, out var parsed) ? parsed : null;
}

static int? TryIntFromEnv(string key)
{
    var raw = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    return int.TryParse(raw, out var parsed) ? parsed : null;
}

static string GetOrCreateIdempotency(IReadOnlyDictionary<string, string> options) =>
    TryGet(options, "idempotency-key") ?? Guid.NewGuid().ToString("N");

static void PrintHelp()
{
    Console.WriteLine("Octocon CLI (Phase D)");
    Console.WriteLine();
    Console.WriteLine("Global options:");
    Console.WriteLine("  --persistence <inmemory|scylla-postgres> (default: scylla-postgres)");
    Console.WriteLine("  --region <nam|eur|ocn|sam|sas|gdpr> (default: nam)");
    Console.WriteLine("  --postgres-connection <connection-string>");
    Console.WriteLine("  --scylla-contact-points <host1,host2>");
    Console.WriteLine("  --scylla-keyspace <name> (default: --region value)");
    Console.WriteLine("  --scylla-datacenter <name>");
    Console.WriteLine("  --scylla-username <name>");
    Console.WriteLine("  --scylla-password <password>");
    Console.WriteLine("  --db-retry-attempts <n> (default: 3)");
    Console.WriteLine("  --db-retry-initial-delay-ms <n> (default: 100)");
    Console.WriteLine("  --db-retry-max-delay-ms <n> (default: 1500)");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  bootstrap-check");
    Console.WriteLine("  account-username-update --system <id> --username <name> [--idempotency-key <key>] [--expected-version <n>]");
    Console.WriteLine("  alter-create --system <id> --name <name> [--idempotency-key <key>] [--expected-version <n>]");
    Console.WriteLine("  alter-update --system <id> --alter-id <id> [--name <v>] [--alias <v>] [--idempotency-key <key>] [--expected-version <n>]");
    Console.WriteLine("  front-start --system <id> --alter-id <id> [--comment <v>] [--idempotency-key <key>] [--expected-version <n>]");
    Console.WriteLine("  front-end --system <id> --alter-id <id> [--idempotency-key <key>] [--expected-version <n>]");
    Console.WriteLine("  front-primary --system <id> [--alter-id <id>] [--idempotency-key <key>] [--expected-version <n>]");
}

internal sealed class CliRuntime
{
    public UpdateUsernameCommandHandler UpdateUsernameHandler { get; }
    public CreateAlterCommandHandler CreateAlterHandler { get; }
    public UpdateAlterCommandHandler UpdateAlterHandler { get; }
    public StartFrontCommandHandler StartFrontHandler { get; }
    public EndFrontCommandHandler EndFrontHandler { get; }
    public SetPrimaryFrontCommandHandler SetPrimaryFrontHandler { get; }
    public IDatabaseBootstrapHealthChecker BootstrapHealthChecker { get; }

    public CliRuntime(
        UpdateUsernameCommandHandler updateUsernameHandler,
        CreateAlterCommandHandler createAlterHandler,
        UpdateAlterCommandHandler updateAlterHandler,
        StartFrontCommandHandler startFrontHandler,
        EndFrontCommandHandler endFrontHandler,
        SetPrimaryFrontCommandHandler setPrimaryFrontHandler,
        IDatabaseBootstrapHealthChecker bootstrapHealthChecker
    )
    {
        UpdateUsernameHandler = updateUsernameHandler;
        CreateAlterHandler = createAlterHandler;
        UpdateAlterHandler = updateAlterHandler;
        StartFrontHandler = startFrontHandler;
        EndFrontHandler = endFrontHandler;
        SetPrimaryFrontHandler = setPrimaryFrontHandler;
        BootstrapHealthChecker = bootstrapHealthChecker;
    }
}
