using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Octocon.Api;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Friendships;
using Octocon.Domain.Fronting;
using Octocon.Domain.Journals;
using Octocon.Domain.Polls;
using Octocon.Domain.Settings;
using Octocon.Domain.Tags;
using Octocon.Infrastructure.DependencyInjection;
using Octocon.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

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
    cfg.ScyllaKeyspace        = Env("OCTOCON_SCYLLA_KEYSPACE") ?? cfg.ScyllaKeyspace;
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

// --- MVC ---
builder.Services.AddControllers();

var app = builder.Build();

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

