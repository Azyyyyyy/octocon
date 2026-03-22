using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Net.WebSockets;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Octocon.Api;
using Octocon.Api.Auth;
using Octocon.Api.Socket;
using Octocon.Api.Services;
using Octocon.Api.Swagger;
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
using System.Text.Json;

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
    socketApp.Run(HandleUserSocketAsync);
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

static async Task HandleUserSocketAsync(HttpContext context)
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "WebSocket upgrade required.",
            code = "websocket_upgrade_required"
        });
        return;
    }

    var token = context.Request.Query["token"].ToString();
    if (string.IsNullOrWhiteSpace(token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Missing socket token.",
            code = "missing_socket_token"
        });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var buffer = new byte[1024 * 16];
    var batchedInitThresholdBytes = ReadPositiveIntFromEnv("OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD", 1_048_576);
    var joinedTopics = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    string? joinedSystemId = null;
    var topicReplyAsArrayFrame = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    var topicJoinReference = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
    using var sendGate = new SemaphoreSlim(1, 1);
    using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var eventBus = context.RequestServices.GetRequiredService<IClusterEventBus>();
    var frontingRepository = context.RequestServices.GetRequiredService<IFrontingRepository>();
    var alterRepository = context.RequestServices.GetRequiredService<IAlterRepository>();
    var tagRepository = context.RequestServices.GetRequiredService<ITagRepository>();
    var settingsFieldRepository = context.RequestServices.GetRequiredService<ISettingsFieldRepository>();
    var frontingPushTask = PumpFrontingPushesAsync(
        socket,
        eventBus,
        frontingRepository,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);
    var alterPushTask = PumpAlterPushesAsync(
        socket,
        eventBus,
        alterRepository,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);
    var tagPushTask = PumpTagPushesAsync(
        socket,
        eventBus,
        tagRepository,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);
    var fieldsPushTask = PumpSettingsFieldsPushesAsync(
        socket,
        eventBus,
        settingsFieldRepository,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);
    var friendshipPushTask = PumpFriendshipPushesAsync(
        socket,
        eventBus,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);
    var rawPushTask = PumpRawPushesAsync(
        socket,
        eventBus,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);

    while (socket.State == WebSocketState.Open)
    {
        var incomingText = await ReceiveSocketTextAsync(socket, buffer, context.RequestAborted);
        if (incomingText is null)
        {
            break;
        }

        if (!TryParsePhoenixFrame(incomingText, out var eventName, out var topic, out var payload, out var reference, out var joinReference, out var replyAsArrayFrame))
        {
            // Unrecognised frame; close with a protocol-error code rather than
            // echoing raw JSON (which is itself not a valid Phoenix frame).
            await socket.CloseAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                "invalid_phoenix_frame",
                context.RequestAborted);
            break;
        }

        if (string.Equals(eventName, "heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            await SendPhoenixReplyAsync(
                socket,
                topic,
                reference,
                joinReference,
                status: "ok",
                responseJson: "{}",
                replyAsArrayFrame,
                context.RequestAborted,
                sendGate);
            continue;
        }

        if (string.Equals(eventName, "phx_join", StringComparison.OrdinalIgnoreCase))
        {
            var payloadToken = string.Empty;
            var isReconnect = false;
            var forceBatch = false;
            var platform = "unknown";
            var protocolVersion = new Version(1, 0, 0);
            var protocolSupported = true;
            if (payload?.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.Value.TryGetProperty("token", out var tokenProp)
                && tokenProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                payloadToken = tokenProp.GetString() ?? string.Empty;
            }

            if (payload?.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.Value.TryGetProperty("isReconnect", out var isReconnectProp)
                && isReconnectProp.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                isReconnect = isReconnectProp.GetBoolean();
            }

            if (payload?.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.Value.TryGetProperty("forceBatch", out var forceBatchProp)
                && forceBatchProp.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                forceBatch = forceBatchProp.GetBoolean();
            }

            if (payload?.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.Value.TryGetProperty("platform", out var platformProp)
                && platformProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                platform = platformProp.GetString() ?? "unknown";
            }

            if (payload?.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.Value.TryGetProperty("protocolVersion", out var protocolVersionProp)
                && protocolVersionProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var rawVersion = protocolVersionProp.GetString();
                if (!TryParseLooseVersion(rawVersion, out protocolVersion))
                {
                    protocolSupported = false;
                }
            }

            var isSystemTopic = !string.IsNullOrWhiteSpace(topic)
                && topic.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
                && topic.Length > "system:".Length;

            var requestedSystemId = isSystemTopic ? topic["system:".Length..] : string.Empty;
            var tokenAuthorized = IsSocketJoinTokenAuthorized(
                context,
                token,
                requestedSystemId,
                out var tokenAuthFailureReason);

            if (!protocolSupported)
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    responseJson: "{\"reason\":\"unsupported_protocol_version\"}",
                    replyAsArrayFrame,
                        context.RequestAborted,
                        sendGate);
            }
            else if (isSystemTopic
                && string.Equals(payloadToken, token, StringComparison.Ordinal)
                && tokenAuthorized)
            {
                if (!SocketJoinRateLimiter.Allow(requestedSystemId, DateTimeOffset.UtcNow))
                {
                    await SendPhoenixReplyAsync(
                        socket,
                        topic,
                        reference,
                        joinReference,
                        status: "error",
                        responseJson: "{\"reason\":\"rate_limited\"}",
                        replyAsArrayFrame,
                        context.RequestAborted,
                        sendGate);
                    continue;
                }

                joinedTopics[topic] = 0;
                joinedSystemId = requestedSystemId;
                topicReplyAsArrayFrame[topic] = replyAsArrayFrame;
                topicJoinReference[topic] = joinReference;

                var initJson = await BuildJoinInitJsonAsync(context, joinedSystemId, context.RequestAborted);
                var useBatchedInit = false;

                if (!isReconnect)
                {
                    var estimatedEncodedBytes = (int)(Encoding.UTF8.GetByteCount(initJson) * 1.1);
                    useBatchedInit = forceBatch
                        || (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
                            && estimatedEncodedBytes > batchedInitThresholdBytes
                            && protocolVersion >= new Version(2, 0, 0));
                }

                string joinResponseJson;
                if (isReconnect)
                {
                    using var reconnectDoc = JsonDocument.Parse(initJson);
                    var reconnectSystemJson = reconnectDoc.RootElement.TryGetProperty("system", out var reconnectSystemEl)
                        ? reconnectSystemEl.GetRawText()
                        : "null";
                    joinResponseJson = "{\"system\":" + reconnectSystemJson + "}";
                }
                else if (useBatchedInit)
                {
                    using var initDoc = JsonDocument.Parse(initJson);
                    var systemJson = initDoc.RootElement.TryGetProperty("system", out var systemEl)
                        ? systemEl.GetRawText()
                        : "null";

                    joinResponseJson =
                        "{" +
                        "\"batched\":true," +
                        "\"system\":" + systemJson + "," +
                        "\"alters\":null," +
                        "\"fronts\":null," +
                        "\"tags\":null" +
                        "}";
                }
                else
                {
                    joinResponseJson = initJson;
                }

                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "ok",
                    responseJson: joinResponseJson,
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);

                if (useBatchedInit)
                {
                    await SendBatchedInitAsync(
                        socket,
                        topic,
                        topicJoinReference[topic],
                        topicReplyAsArrayFrame[topic],
                        initJson,
                        context.RequestAborted,
                        sendGate);
                }
            }
            else
            {
                var unauthorizedReason = tokenAuthFailureReason is null
                    ? "unauthorized"
                    : tokenAuthFailureReason;
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    responseJson: SerializeSocketJson(new { reason = unauthorizedReason }),
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);
            }

            continue;
        }

        if (string.Equals(eventName, "endpoint", StringComparison.OrdinalIgnoreCase))
        {
            if (!joinedTopics.ContainsKey(topic))
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    responseJson: "{\"reason\":\"not_joined\"}",
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);
                continue;
            }

            var endpointResult = await HandleEndpointProxyAsync(context, payload, token, joinedSystemId);
            var endpointResponseJson = System.Text.Json.JsonSerializer.Serialize(endpointResult);

            await SendPhoenixReplyAsync(
                socket,
                topic,
                reference,
                joinReference,
                status: "ok",
                responseJson: endpointResponseJson,
                replyAsArrayFrame,
                context.RequestAborted,
                sendGate);
            continue;
        }

        await SendPhoenixReplyAsync(
            socket,
            topic,
            reference,
            joinReference,
            status: "error",
            responseJson: "{\"reason\":\"event_not_implemented\"}",
            replyAsArrayFrame,
            context.RequestAborted,
            sendGate);
    }

    pushCts.Cancel();
    try
    {
        await Task.WhenAll(frontingPushTask, alterPushTask, tagPushTask, fieldsPushTask, friendshipPushTask, rawPushTask);
    }
    catch (OperationCanceledException)
    {
        // Expected on socket shutdown.
    }

    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
    {
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "socket closed",
            CancellationToken.None);
    }
}

static async Task<object> HandleEndpointProxyAsync(
    HttpContext websocketContext,
    System.Text.Json.JsonElement? payload,
    string socketToken,
    string? joinedSystemId)
{
    if (payload is null || payload.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
    {
        return new
        {
            status = StatusCodes.Status400BadRequest,
            body = "{\"error\":\"Invalid endpoint payload.\",\"code\":\"socket_endpoint_payload_invalid\"}"
        };
    }

    var payloadObj = payload.Value;
    var method = payloadObj.TryGetProperty("method", out var methodProp)
                 && methodProp.ValueKind == System.Text.Json.JsonValueKind.String
        ? methodProp.GetString() ?? string.Empty
        : string.Empty;

    var path = payloadObj.TryGetProperty("path", out var pathProp)
               && pathProp.ValueKind == System.Text.Json.JsonValueKind.String
        ? pathProp.GetString() ?? string.Empty
        : string.Empty;

    if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
    {
        return new
        {
            status = StatusCodes.Status400BadRequest,
            body = "{\"error\":\"Endpoint payload must include method and path.\",\"code\":\"socket_endpoint_method_path_required\"}"
        };
    }

    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        return new
        {
            status = StatusCodes.Status403Forbidden,
            body = "{\"error\":\"Socket endpoint relay is restricted to /api paths.\",\"code\":\"socket_endpoint_path_forbidden\"}"
        };
    }

    var targetUri = $"{websocketContext.Request.Scheme}://{websocketContext.Request.Host}{path}";
    using var request = new HttpRequestMessage(new HttpMethod(method), targetUri);
    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {socketToken}");
    request.Headers.TryAddWithoutValidation("Accept", "application/json");

    if (!string.IsNullOrWhiteSpace(joinedSystemId))
    {
        request.Headers.TryAddWithoutValidation("X-Octocon-Dev-Principal", joinedSystemId);
    }

    string? requestBodyJson = null;
    if (payloadObj.TryGetProperty("body", out var bodyProp)
        && bodyProp.ValueKind != System.Text.Json.JsonValueKind.Null
        && method is not "GET" and not "HEAD")
    {
        //This NEEDS to be GetString() because the raw JSON is what we want to forward, not a re-serialized version of the body element.
        requestBodyJson = bodyProp.GetString();
        if (requestBodyJson != null) {
            request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");
        }
    }

    using var httpClient = new HttpClient();
    using var response = await httpClient.SendAsync(request, websocketContext.RequestAborted);
    var responseBody = await response.Content.ReadAsStringAsync(websocketContext.RequestAborted);

    await PublishRelayDomainEventsAsync(
        websocketContext,
        joinedSystemId,
        method,
        path,
        (int)response.StatusCode,
        responseBody,
        requestBodyJson,
        websocketContext.RequestAborted);

    return new
    {
        status = (int)response.StatusCode,
        body = responseBody
    };
}

static bool IsSocketJoinTokenAuthorized(
    HttpContext context,
    string token,
    string requestedSystemId,
    out string? failureReason)
{
    failureReason = null;

    if (string.IsNullOrWhiteSpace(token))
    {
        failureReason = "missing_socket_token";
        return false;
    }

    var settings = context.RequestServices.GetRequiredService<ApiSettings>();
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var jwtAudience = "octocon";
    var jwtSigningSecrets = new[]
    {
        configuration["OCTOCON_AUTH_DEEP_LINK_SECRET"],
        configuration["GUARDIAN_SECRET_KEY"],
        configuration["OCTOCON_JWT_AUTHORITY"],
        "octocon-local"
    }
        .Where(static s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    var jwtSigningKeys = jwtSigningSecrets
        .Select(static secret => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret!)))
        .ToArray();

    var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    if (!handler.CanReadToken(token))
    {
        if (settings.DevPrincipalAllowed)
        {
            return true;
        }

        failureReason = "invalid_socket_token";
        return false;
    }

    var parameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        ValidateIssuerSigningKey = false,
        RequireSignedTokens = true,
        IssuerSigningKeys = jwtSigningKeys,
        IssuerSigningKeyResolver = static (token, securityToken, kid, validationParameters) =>
            validationParameters.IssuerSigningKeys ?? Array.Empty<SecurityKey>(),
        SignatureValidator = ValidateHs256TokenSignatureForSocket,
        NameClaimType = "sub"
    };

    try
    {
        var principal = handler.ValidateToken(token, parameters, out _);
        var tokenSystemId = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(tokenSystemId))
        {
            failureReason = "invalid_socket_token_subject";
            return false;
        }

        if (!string.Equals(tokenSystemId, requestedSystemId, StringComparison.Ordinal))
        {
            failureReason = "unauthorized_topic";
            return false;
        }

        return true;
    }
    catch (Exception)
    {
        failureReason = "invalid_socket_token";
        return false;
    }
}

static async Task<string?> ReceiveSocketTextAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
{
    using var messageStream = new MemoryStream();

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult received;
        try
        {
            received = await socket.ReceiveAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (received.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        if (received.MessageType != WebSocketMessageType.Text)
        {
            return null;
        }

        await messageStream.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken);

        if (received.EndOfMessage)
        {
            return Encoding.UTF8.GetString(messageStream.ToArray());
        }
    }

    return null;
}

static async Task<string> BuildJoinInitJsonAsync(
    HttpContext context,
    string systemId,
    CancellationToken ct)
{
    await using var scope = context.RequestServices.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var (profile, alters, fronts, tags, settingsFields, encryptionState) = await FetchSocketInitDataAsync(
        systemId,
        sp.GetRequiredService<IAccountRepository>(),
        sp.GetRequiredService<IAlterRepository>(),
        sp.GetRequiredService<IFrontingRepository>(),
        sp.GetRequiredService<ITagRepository>(),
        sp.GetRequiredService<ISettingsFieldRepository>(),
        sp.GetRequiredService<IEncryptionStateRepository>(),
        ct);

    var fields = settingsFields
        .OrderBy(x => x.Index)
        .Select(x => (object)new Dictionary<string, object?>
        {
            ["id"] = x.Id,
            ["name"] = x.Name,
            ["type"] = x.Type,
            ["security_level"] = x.SecurityLevel,
            ["locked"] = x.Locked,
            ["index"] = x.Index
        })
        .ToArray();

    var system = new Dictionary<string, object?>
    {
        ["id"] = profile?.SystemId ?? systemId,
        ["username"] = profile?.Username,
        ["description"] = profile?.Description,
        ["avatar_url"] = profile?.AvatarUrl,
        // Keep parity with Elixir's SystemJSON.data_me defaults.
        ["autoproxy_mode"] = "off",
        ["show_system_tag"] = false,
        ["lifetime_alter_count"] = alters.Count,
        ["fields"] = fields,
        ["encryption_initialized"] = encryptionState?.Initialized ?? false
    };

    var initData = new
    {
        system,
        alters,
        fronts,
        tags
    };

    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    return JsonSerializer.Serialize(initData, opts);
}

static async Task SendBatchedInitAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    bool replyAsArrayFrame,
    string initJson,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    using var initDoc = JsonDocument.Parse(initJson);
    var root = initDoc.RootElement;

    var alters = root.TryGetProperty("alters", out var altersEl) && altersEl.ValueKind == JsonValueKind.Array
        ? altersEl.EnumerateArray().Select(x => x.GetRawText()).ToList()
        : [];
    var tags = root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array
        ? tagsEl.EnumerateArray().Select(x => x.GetRawText()).ToList()
        : [];
    var fronts = root.TryGetProperty("fronts", out var frontsEl) && frontsEl.ValueKind == JsonValueKind.Array
        ? frontsEl.EnumerateArray().Select(x => x.GetRawText()).ToList()
        : [];

    await SendBatchedDataAsync(socket, topic, joinReference, replyAsArrayFrame, "batched_init_alters", "alters", 3000, alters, cancellationToken, sendGate);
    await SendBatchedDataAsync(socket, topic, joinReference, replyAsArrayFrame, "batched_init_tags", "tags", 1000, tags, cancellationToken, sendGate);
    await SendBatchedDataAsync(socket, topic, joinReference, replyAsArrayFrame, "batched_init_fronts", "fronts", 50, fronts, cancellationToken, sendGate);

    await SendPhoenixPushAsync(
        socket,
        topic,
        joinReference,
        eventName: "batched_init_complete",
        payloadJson: "{}",
        replyAsArrayFrame,
        cancellationToken,
        sendGate);
}

static async Task SendBatchedDataAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    bool replyAsArrayFrame,
    string eventName,
    string dataName,
    int batchSize,
    List<string> data,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    if (data.Count == 0)
    {
        return;
    }

    var totalBatches = (int)Math.Ceiling((double)data.Count / batchSize);
    for (var i = 0; i < totalBatches; i++)
    {
        if (i > 0)
        {
            await Task.Delay(50, cancellationToken);
        }

        var start = i * batchSize;
        var count = Math.Min(batchSize, data.Count - start);
        var batchItems = string.Join(",", data.GetRange(start, count));
        var payloadJson =
            "{" +
            "\"batch_index\":" + (i + 1) + "," +
            "\"total_batches\":" + totalBatches + "," +
            "\"" + dataName + "\":[" + batchItems + "]" +
            "}";

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinReference,
            eventName,
            payloadJson,
            replyAsArrayFrame,
            cancellationToken,
            sendGate);
    }
}

static async Task SendPhoenixPushAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    string eventName,
    string payloadJson,
    bool replyAsArrayFrame,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    var escapedTopic = JsonSerializer.Serialize(topic);
    var escapedJoinRef = joinReference is null ? "null" : JsonSerializer.Serialize(joinReference);
    var escapedEvent = JsonSerializer.Serialize(eventName);

    var frame = replyAsArrayFrame
        ?
        "[" +
        escapedJoinRef + "," +
        "null," +
        escapedTopic + "," +
        escapedEvent + "," +
        payloadJson +
        "]"
        :
        "{" +
        "\"topic\":" + escapedTopic + "," +
        "\"event\":" + escapedEvent + "," +
        "\"payload\":" + payloadJson + "," +
        "\"ref\":null," +
        "\"join_ref\":" + escapedJoinRef +
        "}";

    var bytes = Encoding.UTF8.GetBytes(frame);
    if (sendGate is not null)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
    else
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}

static async Task PumpFrontingPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingStateChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var fronts = await frontingRepository.ListActiveAsync(evt.SystemId, cancellationToken).ConfigureAwait(false);
        var payloadJson = SerializeSocketJson(new { fronts });

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "fronting_changed",
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

static async Task PumpAlterPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IAlterRepository alterRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<AlterChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "alter_deleted", StringComparison.Ordinal) && evt.AlterId.HasValue)
        {
            payloadJson = SerializeSocketJson(new Dictionary<string, object?> { ["alter_id"] = evt.AlterId.Value });
        }
        else
        {
            if (!evt.AlterId.HasValue)
            {
                continue;
            }

            var alter = await alterRepository.GetAsync(evt.SystemId, evt.AlterId.Value, cancellationToken).ConfigureAwait(false);
            if (alter is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { alter });
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: evt.EventName,
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

static async Task PumpTagPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    ITagRepository tagRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<TagChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "tag_deleted", StringComparison.Ordinal))
        {
            payloadJson = SerializeSocketJson(new Dictionary<string, object?> { ["tag_id"] = evt.TagId });
        }
        else
        {
            var tag = await tagRepository.GetAsync(evt.SystemId, evt.TagId, cancellationToken).ConfigureAwait(false);
            if (tag is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { tag });
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: evt.EventName,
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

static async Task PumpSettingsFieldsPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    ISettingsFieldRepository fieldRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SettingsFieldsChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var fields = await fieldRepository.ListAsync(evt.SystemId, cancellationToken).ConfigureAwait(false);
        var payloadJson = SerializeSocketJson(new { fields });

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "fields_updated",
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

static async Task PumpRawPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SocketRawPushEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: evt.EventName,
            evt.PayloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

static async Task PumpFriendshipPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FriendshipSocketEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.TargetSystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        var payloadJson = SerializeSocketJson(new Dictionary<string, object?>
        {
            [evt.PayloadKey] = evt.PayloadValue
        });

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            evt.EventName,
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

static async Task PublishRelayDomainEventsAsync(
    HttpContext context,
    string? systemId,
    string method,
    string path,
    int statusCode,
    string responseBody,
    string? requestBodyJson,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(systemId)
        || statusCode is < 200 or >= 300
        || string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var eventBus = context.RequestServices.GetRequiredService<IClusterEventBus>();

    // ── Alters ────────────────────────────────────────────────────────────
    if (path.StartsWith("/api/systems/me/alters", StringComparison.OrdinalIgnoreCase))
    {
        // Alter journals live under /api/systems/me/alters/{id}/journals – handle first
        if (path.Contains("/journals", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAlterJournalRelayAsync(eventBus, context, systemId, method, path, responseBody, ct);
            return;
        }
        // Non-journal alter events are published by domain command handlers.
        return;
    }

    // ── Tags ──────────────────────────────────────────────────────────────
    if (path.StartsWith("/api/systems/me/tags", StringComparison.OrdinalIgnoreCase))
    {
        // Tag events are published by domain command handlers.
        return;
    }

    // ── Settings: custom fields ───────────────────────────────────────────
    if (path.StartsWith("/api/settings/fields", StringComparison.OrdinalIgnoreCase))
    {
        // settings field events are published by domain command handlers.
        return;
    }

    // ── Settings: account management ──────────────────────────────────────
    if (path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase))
    {
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/delete-account", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "account_deleted", "{}"), ct);
                return;
            }
            if (path.EndsWith("/wipe-alters", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alters_wiped", "{}"), ct);
                return;
            }
            if (path.EndsWith("/reset-encryption", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "encrypted_data_wiped", "{}"), ct);
                return;
            }
            if (path.EndsWith("/unlink_discord", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "discord_account_unlinked", "{}"), ct);
                return;
            }
            if (path.EndsWith("/unlink_apple", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "apple_account_unlinked", "{}"), ct);
                return;
            }
        }

        // Profile-mutating operations → self_updated (and username_updated when applicable)
        var isAvatarWrite = path.EndsWith("/avatar", StringComparison.OrdinalIgnoreCase);
        var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
        var emitsProfileUpdate =
            isAvatarWrite ||
            (isPost && (
                path.EndsWith("/username", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/description", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/import-pk", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/import-sp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/setup-encryption", StringComparison.OrdinalIgnoreCase)));

        if (emitsProfileUpdate)
        {
            var accountRepo = context.RequestServices.GetRequiredService<IAccountRepository>();
            var profile = await accountRepo.GetPublicProfileAsync(systemId, ct);
            if (isPost && path.EndsWith("/username", StringComparison.OrdinalIgnoreCase) && profile?.Username != null)
            {
                var usernamePayload = SerializeSocketJson(new { username = profile.Username });
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "username_updated", usernamePayload), ct);
            }
            var selfData = new Dictionary<string, object?>
            {
                ["id"] = profile?.SystemId ?? systemId,
                ["username"] = profile?.Username,
                ["description"] = profile?.Description,
                ["avatar_url"] = profile?.AvatarUrl,
                ["autoproxy_mode"] = "off",
                ["show_system_tag"] = false
            };
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "self_updated", SerializeSocketJson(new { data = selfData })), ct);
        }

        return;
    }

    // ── Fronting ──────────────────────────────────────────────────────────
    if (path.StartsWith("/api/systems/me/front", StringComparison.OrdinalIgnoreCase))
    {
        // primary_front: SetPrimaryFrontCommandHandler already publishes FrontingStateChangedEvent
        // but we also want to emit the specific primary_front event
        if (path.EndsWith("/primary", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            int? alterIdForPrimary = null;
            if (!string.IsNullOrEmpty(requestBodyJson))
            {
                try
                {
                    using var reqDoc = JsonDocument.Parse(requestBodyJson);
                    if (reqDoc.RootElement.TryGetProperty("alter_id", out var aidProp) && aidProp.TryGetInt32(out var aid))
                        alterIdForPrimary = aid;
                    else if (reqDoc.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id2))
                        alterIdForPrimary = id2;
                }
                catch (JsonException) { }
            }
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "primary_front",
                SerializeSocketJson(new { alter_id = alterIdForPrimary })), ct);

            // Also emit self_updated since set_primary_front mutates the user record
            var accountRepo = context.RequestServices.GetRequiredService<IAccountRepository>();
            var profile = await accountRepo.GetPublicProfileAsync(systemId, ct);
            var selfData = new Dictionary<string, object?>
            {
                ["id"] = profile?.SystemId ?? systemId,
                ["username"] = profile?.Username,
                ["description"] = profile?.Description,
                ["avatar_url"] = profile?.AvatarUrl,
                ["autoproxy_mode"] = "off",
                ["show_system_tag"] = false
            };
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "self_updated", SerializeSocketJson(new { data = selfData })), ct);
            return;
        }

        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && TryExtractTerminalString(path, "/api/systems/me/front", out var deletedFrontId)
            && !string.IsNullOrWhiteSpace(deletedFrontId)
            && char.IsDigit(deletedFrontId[0]))
        {
            // front_deleted with the front ID
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "front_deleted",
                SerializeSocketJson(new { front_id = deletedFrontId })), ct);
            // FrontingStateChangedEvent already fired by DeleteFrontByIdCommandHandler (added in todo 9)
            return;
        }

        // POST /api/systems/me/front/{id}/comment → front_updated
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/comment", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // segments: [api, systems, me, front, {id}, comment]
            if (segments.Length >= 2 && int.TryParse(segments[^2], out var frontIdForComment))
            {
                var frontingRepo = context.RequestServices.GetRequiredService<IFrontingRepository>();
                var front = await frontingRepo.GetActiveByFrontIdAsync(systemId, frontIdForComment.ToString(), ct);
                if (front is not null)
                {
                    await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "front_updated",
                        SerializeSocketJson(new { front })), ct);
                }
            }
            return;
        }

        // bulk update (POST /api/systems/me/front) and set (POST /api/systems/me/front/set)
        // FrontingStateChangedEvent is published by the command handlers (added in todo 9)
        return;
    }

    // ── Polls ─────────────────────────────────────────────────────────────
    if (path.StartsWith("/api/polls", StringComparison.OrdinalIgnoreCase))
    {
        var pollRepo = context.RequestServices.GetRequiredService<IPollRepository>();

        if (statusCode is 201
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/api/polls", StringComparison.OrdinalIgnoreCase))
        {
            var poll = TryExtractEntityFromResponse(responseBody);
            if (poll is not null)
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "poll_created", "{\"poll\":" + poll + "}"), ct);
            return;
        }

        if (TryExtractTerminalString(path, "/api/polls", out var pollId))
        {
            if (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                var poll = await pollRepo.GetAsync(systemId, pollId, ct);
                if (poll is not null)
                    await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "poll_updated",
                        SerializeSocketJson(new { poll })), ct);
                return;
            }
            if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "poll_deleted",
                    SerializeSocketJson(new { poll_id = pollId })), ct);
                return;
            }
        }

        return;
    }

    // ── Global journals ───────────────────────────────────────────────────
    if (path.StartsWith("/api/journals", StringComparison.OrdinalIgnoreCase))
    {
        await HandleGlobalJournalRelayAsync(eventBus, context, systemId, method, path, responseBody, ct);
        return;
    }

    // ── Friends ───────────────────────────────────────────────────────────
    if (path.StartsWith("/api/friends", StringComparison.OrdinalIgnoreCase))
    {
        // Friend events are published by domain command handlers.
        return;
    }

    // ── Friend requests ───────────────────────────────────────────────────
    if (path.StartsWith("/api/friend-requests", StringComparison.OrdinalIgnoreCase))
    {
        // Friend request events are published by domain command handlers.
        return;
    }
}

// Helper: extract the raw JSON of the "data" field from a response body
static string? TryExtractEntityFromResponse(string responseBody)
{
    try
    {
        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.TryGetProperty("data", out var data)
            ? data.GetRawText()
            : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

// Helper: journal fan-out for global journals
static async Task HandleGlobalJournalRelayAsync(
    IClusterEventBus eventBus,
    HttpContext context,
    string systemId,
    string method,
    string path,
    string responseBody,
    CancellationToken ct)
{
    var journalRepo = context.RequestServices.GetRequiredService<IJournalRepository>();

    // POST /api/journals → create
    if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && string.Equals(path, "/api/journals", StringComparison.OrdinalIgnoreCase))
    {
        var entryRaw = TryExtractEntityFromResponse(responseBody);
        if (entryRaw is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "global_journal_entry_created",
                "{\"entry\":" + entryRaw + "}"), ct);
        return;
    }

    if (TryExtractTerminalString(path, "/api/journals", out var entryId))
    {
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "global_journal_entry_deleted",
                SerializeSocketJson(new { entry_id = entryId })), ct);
            return;
        }

        // PATCH or POST sub-operations (alters, locked, pinned) → entry updated
        var entry = await journalRepo.GetGlobalAsync(systemId, entryId, ct);
        if (entry is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "global_journal_entry_updated",
                SerializeSocketJson(new { entry })), ct);
    }
}

// Helper: journal fan-out for alter journals (/api/systems/me/alters/{alterId}/journals/...)
static async Task HandleAlterJournalRelayAsync(
    IClusterEventBus eventBus,
    HttpContext context,
    string systemId,
    string method,
    string path,
    string responseBody,
    CancellationToken ct)
{
    var journalRepo = context.RequestServices.GetRequiredService<IJournalRepository>();

    // Find the journals segment index to extract entry ID
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    // segments: [api, systems, me, alters, {alterId}, journals[, {entryId}]]
    var journalsIdx = Array.FindIndex(segments, s => string.Equals(s, "journals", StringComparison.OrdinalIgnoreCase));
    if (journalsIdx < 0) return;

    var isCreate = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && journalsIdx == segments.Length - 1;

    if (isCreate)
    {
        var entryRaw = TryExtractEntityFromResponse(responseBody);
        if (entryRaw is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alter_journal_entry_created",
                "{\"entry\":" + entryRaw + "}"), ct);
        return;
    }

    if (journalsIdx + 1 < segments.Length)
    {
        var entryId = segments[journalsIdx + 1];
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alter_journal_entry_deleted",
                SerializeSocketJson(new { entry_id = entryId })), ct);
            return;
        }

        var entry = await journalRepo.GetAlterAsync(systemId, entryId, ct);
        if (entry is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alter_journal_entry_updated",
                SerializeSocketJson(new { entry })), ct);
    }
}

static bool TryExtractTerminalString(string path, string prefix, out string segment)
{
    segment = string.Empty;
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var tail = path[prefix.Length..].Trim('/');
    if (string.IsNullOrWhiteSpace(tail))
    {
        return false;
    }

    var first = tail.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
    if (string.IsNullOrWhiteSpace(first))
    {
        return false;
    }

    segment = first;
    return true;
}

static string SerializeSocketJson(object value)
{
    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    return JsonSerializer.Serialize(value, opts);
}

static bool TryParseLooseVersion(string? value, out Version parsed)
{
    parsed = new Version(1, 0, 0);
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    var normalized = value.Trim();
    if (Version.TryParse(normalized, out parsed!))
    {
        return true;
    }

    // Phoenix clients typically use semver strings like "2.0.0";
    // allow a trailing prerelease segment by stripping from '-'.
    var dashIndex = normalized.IndexOf('-');
    if (dashIndex > 0)
    {
        var stable = normalized[..dashIndex];
        return Version.TryParse(stable, out parsed!);
    }

    return false;
}

static SecurityToken ValidateHs256TokenSignatureForSocket(string token, TokenValidationParameters validationParameters)
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

    var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
    using (var headerDoc = JsonDocument.Parse(headerJson))
    {
        if (!headerDoc.RootElement.TryGetProperty("alg", out var algProp)
            || !string.Equals(algProp.GetString(), "HS256", StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidSignatureException("Unsupported JWT algorithm.");
        }
    }

    var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
    var signatureBytes = Base64UrlDecode(parts[2]);

    var keys = validationParameters.IssuerSigningKeys?.OfType<SymmetricSecurityKey>().ToArray()
        ?? Array.Empty<SymmetricSecurityKey>();

    foreach (var key in keys)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(key.Key);
        var computed = hmac.ComputeHash(signingInput);
        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computed, signatureBytes))
        {
            return new JwtSecurityToken(token);
        }
    }

    throw new SecurityTokenInvalidSignatureException("Invalid JWT signature.");
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

    var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
    using (var headerDoc = JsonDocument.Parse(headerJson))
    {
        if (!headerDoc.RootElement.TryGetProperty("alg", out var algProp)
            || !string.Equals(algProp.GetString(), "HS256", StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidSignatureException("Unsupported JWT algorithm.");
        }
    }

    var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
    var signatureBytes = Base64UrlDecode(parts[2]);

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

static byte[] Base64UrlDecode(string value)
{
    var padded = value.Replace('-', '+').Replace('_', '/');
    switch (padded.Length % 4)
    {
        case 2:
            padded += "==";
            break;
        case 3:
            padded += "=";
            break;
    }

    return Convert.FromBase64String(padded);
}

static int ReadPositiveIntFromEnv(string key, int fallback)
{
    var raw = Env(key);
    return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
}

static async Task<(
    AccountPublicProfileReadModel? profile,
    IReadOnlyList<AlterReadModel> alters,
    IReadOnlyList<FrontActiveReadModel> fronts,
    IReadOnlyList<TagPublicReadModel> tags,
    IReadOnlyList<SettingsFieldReadModel> settingsFields,
    EncryptionState? encryptionState)>
FetchSocketInitDataAsync(
    string systemId,
    IAccountRepository accounts,
    IAlterRepository alters,
    IFrontingRepository fronting,
    ITagRepository tags,
    ISettingsFieldRepository settingsFields,
    IEncryptionStateRepository encryptionStates,
    CancellationToken ct)
{
    var profileTask = accounts.GetPublicProfileAsync(systemId, ct);
    var altersTask  = alters.ListAsync(systemId, ct);
    var frontsTask  = fronting.ListActiveAsync(systemId, ct);
    var tagsTask    = tags.ListAsync(systemId, ct);
    var fieldsTask  = settingsFields.ListAsync(systemId, ct);
    var encryptionTask = encryptionStates.GetAsync(systemId, ct);
    await Task.WhenAll(profileTask, altersTask, frontsTask, tagsTask, fieldsTask, encryptionTask);
    return (profileTask.Result, altersTask.Result, frontsTask.Result, tagsTask.Result, fieldsTask.Result, encryptionTask.Result);
}

static bool TryParsePhoenixFrame(
    string frame,
    out string eventName,
    out string topic,
    out System.Text.Json.JsonElement? payload,
    out string? reference,
    out string? joinReference,
    out bool replyAsArrayFrame)
{
    eventName = string.Empty;
    topic = "phoenix";
    payload = null;
    reference = null;
    joinReference = null;
    replyAsArrayFrame = false;

    var trimmed = frame.TrimStart();

    if (trimmed.StartsWith("[", StringComparison.Ordinal))
    {
        try
        {
            using var arrayDoc = System.Text.Json.JsonDocument.Parse(frame);
            var root = arrayDoc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array || root.GetArrayLength() < 5)
            {
                return false;
            }

            var joinRefElement = root[0];
            var refElement = root[1];
            var topicElement = root[2];
            var eventElement = root[3];

            if (topicElement.ValueKind != System.Text.Json.JsonValueKind.String
                || eventElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return false;
            }

            // Preserve JSON null so replies can mirror it back as null (not "").
            joinReference = joinRefElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? joinRefElement.GetString()
                : null;

            reference = refElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? refElement.GetString()
                : null;

            topic = topicElement.GetString() ?? topic;
            eventName = eventElement.GetString() ?? string.Empty;
            payload = root[4].Clone();
            replyAsArrayFrame = true;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(frame);
        var root = doc.RootElement;

        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("event", out var eventProp)
            || eventProp.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }

        eventName = eventProp.GetString() ?? string.Empty;

        if (root.TryGetProperty("topic", out var topicProp)
            && topicProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            topic = topicProp.GetString() ?? topic;
        }

        if (root.TryGetProperty("payload", out var payloadProp))
        {
            payload = payloadProp.Clone();
        }

        if (root.TryGetProperty("ref", out var refProp)
            && refProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            reference = refProp.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("join_ref", out var joinRefProp)
            && joinRefProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            joinReference = joinRefProp.GetString() ?? string.Empty;
        }

        return true;
    }
    catch (System.Text.Json.JsonException)
    {
        return false;
    }
}

static async Task SendPhoenixReplyAsync(
    WebSocket socket,
    string topic,
    string? reference,
    string? joinReference,
    string status,
    string responseJson,
    bool replyAsArrayFrame,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    var escapedTopic = System.Text.Json.JsonSerializer.Serialize(topic);
    // Preserve null so the client receives null rather than empty-string, which
    // Phoenix clients use to distinguish heartbeat replies from channel pushes.
    var escapedRef = reference is null ? "null" : System.Text.Json.JsonSerializer.Serialize(reference);
    var escapedJoinRef = joinReference is null ? "null" : System.Text.Json.JsonSerializer.Serialize(joinReference);
    var escapedStatus = System.Text.Json.JsonSerializer.Serialize(status);
    var escapedEvent = System.Text.Json.JsonSerializer.Serialize("phx_reply");

    var payloadJson =
        "{" +
        "\"status\":" + escapedStatus + "," +
        "\"response\":" + responseJson +
        "}";

    var frame = replyAsArrayFrame
        ?
        "[" +
        escapedJoinRef + "," +
        escapedRef + "," +
        escapedTopic + "," +
        escapedEvent + "," +
        payloadJson +
        "]"
        :
        "{" +
        "\"topic\":" + escapedTopic + "," +
        "\"event\":\"phx_reply\"," +
        "\"payload\":" + payloadJson + "," +
        "\"ref\":" + escapedRef + "," +
        "\"join_ref\":" + escapedJoinRef +
        "}";

    var bytes = Encoding.UTF8.GetBytes(frame);
    if (sendGate is not null)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
    else
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}

static class SocketJoinRateLimiter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows =
        new(StringComparer.Ordinal);

    public static bool Allow(string systemId, DateTimeOffset now)
    {
        var queue = _windows.GetOrAdd(systemId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var cutoff = now.AddSeconds(-1);
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= 2)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}

