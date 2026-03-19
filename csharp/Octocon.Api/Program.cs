using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Net.WebSockets;
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

    var buffer = new byte[4096];
    var joinedTopics = new HashSet<string>(StringComparer.Ordinal);
    string? joinedSystemId = null;

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
                context.RequestAborted);
            continue;
        }

        if (string.Equals(eventName, "phx_join", StringComparison.OrdinalIgnoreCase))
        {
            var payloadToken = string.Empty;
            var isReconnect = false;
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

            var isSystemTopic = !string.IsNullOrWhiteSpace(topic)
                && topic.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
                && topic.Length > "system:".Length;

            var requestedSystemId = isSystemTopic ? topic["system:".Length..] : string.Empty;
            var tokenSystemId = TryReadJwtSubjectUnchecked(token);
            var topicMatchesTokenSubject = string.IsNullOrWhiteSpace(tokenSystemId)
                || string.Equals(tokenSystemId, requestedSystemId, StringComparison.Ordinal);

            if (isSystemTopic
                && string.Equals(payloadToken, token, StringComparison.Ordinal)
                && topicMatchesTokenSubject)
            {
                joinedTopics.Add(topic);
                joinedSystemId = requestedSystemId;

                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "ok",
                    responseJson: isReconnect
                        ? "{}"
                        : await BuildJoinInitJsonAsync(context, joinedSystemId, context.RequestAborted),
                    replyAsArrayFrame,
                    context.RequestAborted);
            }
            else
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    responseJson: "{\"reason\":\"unauthorized\"}",
                        replyAsArrayFrame,
                    context.RequestAborted);
            }

            continue;
        }

        if (string.Equals(eventName, "endpoint", StringComparison.OrdinalIgnoreCase))
        {
            if (!joinedTopics.Contains(topic))
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    responseJson: "{\"reason\":\"not_joined\"}",
                    replyAsArrayFrame,
                    context.RequestAborted);
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
                context.RequestAborted);
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
            context.RequestAborted);
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

    if (payloadObj.TryGetProperty("body", out var bodyProp)
        && bodyProp.ValueKind == System.Text.Json.JsonValueKind.String
        && method is not "GET" and not "HEAD")
    {
        var bodyJson = bodyProp.GetString();
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
    }

    using var httpClient = new HttpClient();
    using var response = await httpClient.SendAsync(request, websocketContext.RequestAborted);
    var responseBody = await response.Content.ReadAsStringAsync(websocketContext.RequestAborted);

    return new
    {
        status = (int)response.StatusCode,
        body = responseBody
    };
}

static string? TryReadJwtSubjectUnchecked(string token)
{
    var parts = token.Split('.');
    if (parts.Length != 3)
    {
        return null;
    }

    try
    {
        var payloadBytes = Base64UrlDecode(parts[1]);
        using var doc = System.Text.Json.JsonDocument.Parse(payloadBytes);
        if (doc.RootElement.TryGetProperty("sub", out var sub)
            && sub.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return sub.GetString();
        }
    }
    catch
    {
        return null;
    }

    return null;
}

static byte[] Base64UrlDecode(string base64Url)
{
    var padded = base64Url.Replace('-', '+').Replace('_', '/');
    padded = (padded.Length % 4) switch
    {
        2 => padded + "==",
        3 => padded + "=",
        _ => padded
    };

    return Convert.FromBase64String(padded);
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
            ["id"] = x.FieldId,
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

static async Task<(
    AccountPublicProfileReadModel? profile,
    IReadOnlyList<AlterPublicReadModel> alters,
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
    CancellationToken cancellationToken)
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
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}

