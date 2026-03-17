using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Octocon.Api;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Friendships;
using Octocon.Domain.Fronting;
using Octocon.Domain.Journals;
using Octocon.Domain.Polls;
using Octocon.Domain.Settings;
using Octocon.Domain.Tags;
using Octocon.Infrastructure.Coordination;
using Octocon.Infrastructure.DependencyInjection;
using Octocon.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// --- Node role ---
// Resolution order mirrors the legacy Elixir runtime:
//   1. FLY_PROCESS_GROUP (fly.io automatic)
//   2. OCTOCON_NODE_GROUP (manual override)
//   3. Default: auxiliary
var nodeGroup = NodeGroupResolver.Resolve();
builder.Services.AddOctoconCluster(nodeGroup);

// --- Persistence ---
var persistenceMode = (Env("OCTOCON_PERSISTENCE") ?? "scylla-postgres").ToLowerInvariant() switch
{
    "inmemory"       => PersistenceMode.InMemory,
    "scylla-postgres" => PersistenceMode.ScyllaPostgres,
    var x            => throw new InvalidOperationException($"Unsupported persistence mode: {x}")
};

builder.Services.AddOctoconPersistence(persistenceMode, cfg =>
{
    cfg.DefaultRegion         = Env("OCTOCON_REGION") ?? cfg.DefaultRegion;
    cfg.PostgresConnectionString = Env("OCTOCON_POSTGRES_CONNECTION") ?? cfg.PostgresConnectionString;
    cfg.ScyllaKeyspace        = Env("OCTOCON_SCYLLA_KEYSPACE") ?? cfg.DefaultRegion;
    cfg.ScyllaLocalDatacenter = Env("OCTOCON_SCYLLA_DATACENTER") ?? cfg.ScyllaLocalDatacenter;

    var contactPoints = Env("OCTOCON_SCYLLA_CONTACT_POINTS");
    if (!string.IsNullOrWhiteSpace(contactPoints))
        cfg.ScyllaContactPoints = contactPoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    cfg.ScyllaUsername = Env("OCTOCON_SCYLLA_USERNAME");
    cfg.ScyllaPassword = Env("OCTOCON_SCYLLA_PASSWORD");
});

// --- Domain handlers ---
builder.Services.AddSingleton<CreateAlterCommandHandler>();
builder.Services.AddSingleton<UpdateAlterCommandHandler>();
builder.Services.AddSingleton<DeleteAlterCommandHandler>();
builder.Services.AddSingleton<StartFrontCommandHandler>();
builder.Services.AddSingleton<EndFrontCommandHandler>();
builder.Services.AddSingleton<BulkUpdateFrontCommandHandler>();
builder.Services.AddSingleton<SetFrontCommandHandler>();
builder.Services.AddSingleton<SetPrimaryFrontCommandHandler>();
builder.Services.AddSingleton<DeleteFrontByIdCommandHandler>();
builder.Services.AddSingleton<UpdateFrontCommentCommandHandler>();
builder.Services.AddSingleton<UpdateUsernameCommandHandler>();
builder.Services.AddSingleton<UpdateDescriptionCommandHandler>();
builder.Services.AddSingleton<AddPushTokenCommandHandler>();
builder.Services.AddSingleton<RemovePushTokenCommandHandler>();
builder.Services.AddSingleton<SetupEncryptionCommandHandler>();
builder.Services.AddSingleton<RecoverEncryptionCommandHandler>();
builder.Services.AddSingleton<ResetEncryptionCommandHandler>();
builder.Services.AddSingleton<UploadAvatarCommandHandler>();
builder.Services.AddSingleton<DeleteAvatarCommandHandler>();
builder.Services.AddSingleton<ImportPkCommandHandler>();
builder.Services.AddSingleton<ImportSpCommandHandler>();
builder.Services.AddSingleton<UnlinkDiscordCommandHandler>();
builder.Services.AddSingleton<UnlinkEmailCommandHandler>();
builder.Services.AddSingleton<UnlinkAppleCommandHandler>();
builder.Services.AddSingleton<DeleteAccountCommandHandler>();
builder.Services.AddSingleton<WipeAltersCommandHandler>();
builder.Services.AddSingleton<CreateFieldCommandHandler>();
builder.Services.AddSingleton<UpdateFieldCommandHandler>();
builder.Services.AddSingleton<DeleteFieldCommandHandler>();
builder.Services.AddSingleton<RelocateFieldCommandHandler>();
builder.Services.AddSingleton<CreateTagCommandHandler>();
builder.Services.AddSingleton<UpdateTagCommandHandler>();
builder.Services.AddSingleton<DeleteTagCommandHandler>();
builder.Services.AddSingleton<AttachAlterToTagCommandHandler>();
builder.Services.AddSingleton<DetachAlterFromTagCommandHandler>();
builder.Services.AddSingleton<SetParentTagCommandHandler>();
builder.Services.AddSingleton<RemoveParentTagCommandHandler>();
builder.Services.AddSingleton<CreatePollCommandHandler>();
builder.Services.AddSingleton<UpdatePollCommandHandler>();
builder.Services.AddSingleton<DeletePollCommandHandler>();
builder.Services.AddSingleton<CreateGlobalJournalEntryCommandHandler>();
builder.Services.AddSingleton<UpdateGlobalJournalEntryCommandHandler>();
builder.Services.AddSingleton<DeleteGlobalJournalEntryCommandHandler>();
builder.Services.AddSingleton<SetGlobalJournalLockedCommandHandler>();
builder.Services.AddSingleton<SetGlobalJournalPinnedCommandHandler>();
builder.Services.AddSingleton<AttachAlterToGlobalJournalCommandHandler>();
builder.Services.AddSingleton<DetachAlterFromGlobalJournalCommandHandler>();
builder.Services.AddSingleton<CreateAlterJournalEntryCommandHandler>();
builder.Services.AddSingleton<UpdateAlterJournalEntryCommandHandler>();
builder.Services.AddSingleton<DeleteAlterJournalEntryCommandHandler>();
builder.Services.AddSingleton<SetAlterJournalLockedCommandHandler>();
builder.Services.AddSingleton<SetAlterJournalPinnedCommandHandler>();
builder.Services.AddSingleton<RemoveFriendshipCommandHandler>();
builder.Services.AddSingleton<SetFriendTrustCommandHandler>();
builder.Services.AddSingleton<SendFriendRequestCommandHandler>();
builder.Services.AddSingleton<AcceptFriendRequestCommandHandler>();
builder.Services.AddSingleton<RejectFriendRequestCommandHandler>();
builder.Services.AddSingleton<CancelFriendRequestCommandHandler>();

// --- API settings ---
builder.Services.AddSingleton(new ApiSettings
{
    DevPrincipalAllowed = "true".Equals(Env("OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"), StringComparison.OrdinalIgnoreCase)
});

var devPrincipalAllowed = "true".Equals(Env("OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"), StringComparison.OrdinalIgnoreCase);

// --- Auth (Phase F baseline) ---
var jwtAuthority = Env("OCTOCON_JWT_AUTHORITY");
var jwtAudience = Env("OCTOCON_JWT_AUDIENCE");

if (!devPrincipalAllowed && string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException(
        "OCTOCON_JWT_AUTHORITY must be configured when OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL is false."
    );

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "sub",
            ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience),
            ValidAudience = jwtAudience,
            ValidateIssuer = !string.IsNullOrWhiteSpace(jwtAuthority)
        };

        if (!string.IsNullOrWhiteSpace(jwtAuthority))
            options.Authority = jwtAuthority;
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// --- OpenTelemetry (Phase N) ---
// Traces and metrics are exported via OTLP when OCTOCON_OTLP_ENDPOINT is set.
// Without the env var the SDK still runs in-process so metrics are always available
// for internal /metrics scraping or future export without code changes.
var otlpEndpoint = Env("OCTOCON_OTLP_ENDPOINT");

builder.Services
    .AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(OctoconMetrics.MeterName);

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });

// --- MVC ---
builder.Services.AddControllers();

var app = builder.Build();

// Phase N: correlation ID propagation and structured request logging.
app.UseMiddleware<RequestCorrelationMiddleware>();

// X-Octocon-Contract response header on every response
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers["X-Octocon-Contract"] = "2026-03-v1";
        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();

if (devPrincipalAllowed)
{
    // Local dev shim: permit principal injection from header without JWT.
    app.Use(async (ctx, next) =>
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var devPrincipal = ctx.Request.Headers["X-Octocon-Dev-Principal"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(devPrincipal))
            {
                var identity = new ClaimsIdentity(
                    [new Claim("sub", devPrincipal)],
                    authenticationType: "DevHeader"
                );
                ctx.User = new ClaimsPrincipal(identity);
            }
        }

        await next();
    });
}

app.UseAuthorization();

app.MapControllers();

app.Run();

static string? Env(string key) => Environment.GetEnvironmentVariable(key);

