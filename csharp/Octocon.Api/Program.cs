using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Octocon.Api;
using Octocon.Api.Auth;
using Octocon.Api.Services;
using Octocon.Api.Swagger;
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
using System.Text.Json;
using Octocon.Api.Socket;

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
builder.Services.AddSingleton<IAvatarStorage, LocalAvatarStorage>();
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
bool.TryParse(Env("OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"), out var devPrincipalAllowed);
bool.TryParse(Env("OCTOCON_AUTH_CHALLENGE_ENABLED"), out var authChallengeEnabled);
builder.Services.AddSingleton(new ApiSettings
{
    DevPrincipalAllowed = devPrincipalAllowed,
    AuthChallengeEnabled = authChallengeEnabled,
    AuthChallengeDiscordScheme = Env("OCTOCON_AUTH_CHALLENGE_DISCORD_SCHEME") ?? "oauth-discord",
    AuthChallengeGoogleScheme = Env("OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME") ?? "oauth-google",
    AuthChallengeAppleScheme = Env("OCTOCON_AUTH_CHALLENGE_APPLE_SCHEME") ?? "oauth-apple",
    AuthChallengeDiscordParameters = ParseAuthParameters(Env("OCTOCON_AUTH_CHALLENGE_DISCORD_PARAMS")),
    AuthChallengeGoogleParameters = ParseAuthParameters(Env("OCTOCON_AUTH_CHALLENGE_GOOGLE_PARAMS")),
    AuthChallengeAppleParameters = ParseAuthParameters(Env("OCTOCON_AUTH_CHALLENGE_APPLE_PARAMS")),
    AuthCallbackBaseUrl = Env("OCTOCON_AUTH_CALLBACK_BASE_URL"),
    GoogleOAuthClientId = Env("OCTOCON_GOOGLE_OAUTH_CLIENT_ID"),
    GoogleOAuthClientSecret = Env("OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET"),
    FrontendAddress = Env("OCTOCON_FRONTEND"),
    BetaFrontendAddress = Env("OCTOCON_BETA_FRONTEND"),
    DeepEndpointAddress = Env("OCTOCON_DEEPLINK_ADDRESS")
});

// --- HTTP Client Factory (for OAuth token exchange, etc.) ---
builder.Services.AddHttpClient<GoogleOAuthService>();

// --- Auth (Phase F baseline) ---
// Tokens are always self-issued by this backend (or the Elixir Guardian backend) after
// OAuth provider authentication completes. There is no single external OIDC authority to
// validate against — the provider (Google/Apple/Discord) is only used to identify the user;
// the resulting JWT is issued by Octocon itself. We therefore skip issuer validation and
// rely on audience and lifetime checks only.
var jwtAudience = "octocon";
var jwtSigningSecrets = new[]
{
    Env("OCTOCON_AUTH_DEEP_LINK_SECRET"),
    Env("GUARDIAN_SECRET_KEY"),
    Env("OCTOCON_JWT_AUTHORITY"), // legacy fallback used as secret in token issuance
    "octocon-local" // final local fallback for parity with IssueDeepLinkToken
}
    .Where(static s => !string.IsNullOrWhiteSpace(s))
    .Distinct(StringComparer.Ordinal)
    .ToArray();

var jwtSigningKeys = jwtSigningSecrets
    .Select(static secret => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret!)))
    .ToArray();

SecurityKey[] ResolveJwtSigningKeys(
    string token,
    SecurityToken? securityToken,
    string? kid,
    TokenValidationParameters validationParameters)
    => jwtSigningKeys;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "sub",
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = false,
            RequireSignedTokens = true,
            IssuerSigningKeys = jwtSigningKeys,
            IssuerSigningKeyResolver = ResolveJwtSigningKeys,
            SignatureValidator = ValidateHs256TokenSignatureForBearer
        };
    });

if (authChallengeEnabled)
{
    TryAddRedirectChallengeScheme(
        builder.Services,
        Env("OCTOCON_AUTH_CHALLENGE_DISCORD_SCHEME") ?? "oauth-discord",
        Env("OCTOCON_AUTH_CHALLENGE_DISCORD_ENDPOINT"),
        ParseAuthParameters(Env("OCTOCON_AUTH_CHALLENGE_DISCORD_PARAMS")));

    TryAddRedirectChallengeScheme(
        builder.Services,
        Env("OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME") ?? "oauth-google",
        Env("OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT"),
        ParseAuthParameters(Env("OCTOCON_AUTH_CHALLENGE_GOOGLE_PARAMS")));

    TryAddRedirectChallengeScheme(
        builder.Services,
        Env("OCTOCON_AUTH_CHALLENGE_APPLE_SCHEME") ?? "oauth-apple",
        Env("OCTOCON_AUTH_CHALLENGE_APPLE_ENDPOINT"),
        ParseAuthParameters(Env("OCTOCON_AUTH_CHALLENGE_APPLE_PARAMS")));
}

builder.Services.AddAuthorization() //Builder
        /*.SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build())*/;

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
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// --- Swagger/OpenAPI ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Octocon API",
        Version = "v1",
        Description = "Octocon API - Contract Version: 2026-03-v1"
    });

    options.ResolveConflictingActions(apiDescriptions =>
    {
        var first = apiDescriptions.First();
        var route = first.RelativePath?.ToLowerInvariant();

        // Only allow conflicts for avatar upload endpoints
        var allowedConflicts = new[]
        {
            "api/settings/avatar",
            "api/systems/me/alters/{id}/avatar"
        };

        if (!allowedConflicts.Any(allowed => route?.Contains(allowed) == true))
        {
            var actionNames = string.Join(", ", apiDescriptions.Select(d => $"{d.ActionDescriptor.DisplayName}"));
            throw new NotSupportedException(
                $"Conflicting actions detected for route '{route}': {actionNames}. " +
                "If this is intentional, add the route to the allowedConflicts list in Program.cs.");
        }

        return first;
    });

    options.DocumentFilter<MultipleContentTypeOperationFilter>();

    // Add JWT Bearer authentication to Swagger UI
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token in the text input below.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });

    if (devPrincipalAllowed)
    {
        // Add dev principal header for local development
        options.AddSecurityDefinition("DevPrincipal", new OpenApiSecurityScheme
        {
            Description = "Development-only: Set X-Octocon-Dev-Principal header with user ID",
            Name = "X-Octocon-Dev-Principal",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Scheme = "DevPrincipal"
        });
    }
});

var app = builder.Build();

// Phase N: correlation ID propagation and structured request logging.
app.UseMiddleware<RequestCorrelationMiddleware>();

// --- Swagger UI ---
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Octocon API v1");
    options.RoutePrefix = "swagger";
});

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
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

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

app.Map("/api/socket", socketApp =>
{
    socketApp.Run(WebSocketHandler.HandleUserSocketAsync);
});

app.MapControllers();

app.Run();

static string? Env(string key) => Environment.GetEnvironmentVariable(key);

static Dictionary<string, string>? ParseAuthParameters(string? paramsString)
{
    if (string.IsNullOrWhiteSpace(paramsString))
        return null;

    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var pairs = paramsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    
    foreach (var pair in pairs)
    {
        var keyValue = pair.Split('=', 2, StringSplitOptions.TrimEntries);
        if (keyValue.Length == 2)
        {
            parameters[keyValue[0]] = keyValue[1];
        }
    }

    return parameters.Count > 0 ? parameters : null;
}

static void TryAddRedirectChallengeScheme(
    IServiceCollection services, 
    string scheme, 
    string? endpoint,
    Dictionary<string, string>? additionalParameters = null)
{
    if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(endpoint))
    {
        return;
    }

    services
        .AddAuthentication()
        .AddScheme<RedirectChallengeOptions, RedirectChallengeAuthenticationHandler>(scheme, options =>
        {
            options.AuthorizationEndpoint = endpoint;
            options.AdditionalParameters = additionalParameters;
        });
}

static SecurityToken ValidateHs256TokenSignatureForBearer(string token, TokenValidationParameters validationParameters)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        throw new SecurityTokenInvalidSignatureException("Token is empty.");
    }

    var parts = token.Split('.');
    if (parts.Length != 3)
    {
        throw new SecurityTokenInvalidSignatureException("Token is not a valid JWS compact token.");
    }

    var headerJson = Encoding.UTF8.GetString(parts[0].Base64UrlDecode());
    using (var headerDoc = JsonDocument.Parse(headerJson))
    {
        if (!headerDoc.RootElement.TryGetProperty("alg", out var algProp)
            || !string.Equals(algProp.GetString(), "HS256", StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidSignatureException("Unsupported JWT algorithm.");
        }
    }

    var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
    var signatureBytes = parts[2].Base64UrlDecode();

    var keys = validationParameters.IssuerSigningKeys?.OfType<SymmetricSecurityKey>().ToArray()
        ?? Array.Empty<SymmetricSecurityKey>();

    foreach (var key in keys)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(key.Key);
        var computed = hmac.ComputeHash(signingInput);
        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computed, signatureBytes))
        {
            return new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);
        }
    }

    throw new SecurityTokenInvalidSignatureException("Invalid JWT signature.");
}