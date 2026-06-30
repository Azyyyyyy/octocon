using System.Net.WebSockets;
using System.IdentityModel.Tokens.Jwt;
using Interfold.Domain.Abstractions;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Socket;

public static class WebSocketHandler
{
public static async Task HandleUserSocketAsync(HttpContext context)
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("WebSocketHandler");
    
    logger.LogInformation(
        "WebSocket request received. Method: {Method}, Path: {Path}, IsWebSocketRequest: {IsWSRequest}",
        context.Request.Method,
        context.Request.Path,
        context.WebSockets.IsWebSocketRequest);

    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogWarning("Request is not a WebSocket upgrade request");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "WebSocket upgrade required.",
            code = "websocket_upgrade_required"
        });
        return;
    }

    var token = context.Request.Query["token"].ToString();
    logger.LogInformation("Token from query string length: {TokenLength}", token?.Length ?? 0);
    
    if (string.IsNullOrWhiteSpace(token))
    {
        logger.LogWarning("Missing or empty token in query string");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Missing socket token.",
            code = "missing_socket_token"
        });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var batchedInitThresholdBytes = context.RequestServices
        .GetRequiredService<IOptionsMonitor<SocketConfiguration>>()
        .CurrentValue.BatchBytesThreshold ?? 1_048_576;
    var buffer = new byte[1024 * 16];
    var joinedTopics = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    string? joinedSystemId = null;
    var topicReplyAsArrayFrame = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    var topicJoinReference = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
    using var sendGate = new SemaphoreSlim(1, 1);
    using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var eventBus = context.RequestServices.GetRequiredService<IClusterEventBus>();
    var rateLimiter = context.RequestServices.GetRequiredService<SocketJoinRateLimiter>();
    var frontingRepository = context.RequestServices.GetRequiredService<IFrontingRepository>();
    var alterRepository = context.RequestServices.GetRequiredService<IAlterRepository>();
    var tagRepository = context.RequestServices.GetRequiredService<ITagRepository>();
    var settingsFieldRepository = context.RequestServices.GetRequiredService<ISettingsFieldRepository>();
    var accountRepository = context.RequestServices.GetRequiredService<IAccountRepository>();
    var friendshipRepository = context.RequestServices.GetRequiredService<IFriendshipRepository>();
    var pollRepository = context.RequestServices.GetRequiredService<IPollRepository>();
    var journalRepository = context.RequestServices.GetRequiredService<IJournalRepository>();
    var encryptionStateRepository = context.RequestServices.GetRequiredService<IEncryptionStateRepository>();
    var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}";

    // The per-socket event pump is started on the first successful phx_join (see below).
    // Deferring registration keeps each socket invisible to the bus until it actually
    // identifies a target system; without it, every just-accepted socket added ~38 broadcast
    // writers to the bus and the publisher had to fan every event out to all of them.
    Task? socketPushTask = null;
    // 0 = pump not started, 1 = started. Mutated only via Interlocked.CompareExchange so a
    // burst of phx_join frames that arrives back-to-back (e.g. retried by the client during
    // a flaky connection) can never race past the guard and spin up a second pump.
    var pumpStarted = 0;

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
                response: new EmptyPayload(),
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
            if (payload?.ValueKind == JsonValueKind.Object
                && payload.Value.TryGetProperty("token", out var tokenProp)
                && tokenProp.ValueKind == JsonValueKind.String)
            {
                payloadToken = tokenProp.GetString() ?? string.Empty;
            }

            if (payload?.ValueKind == JsonValueKind.Object
                && payload.Value.TryGetProperty("isReconnect", out var isReconnectProp)
                && isReconnectProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isReconnect = isReconnectProp.GetBoolean();
            }

            if (payload?.ValueKind == JsonValueKind.Object
                && payload.Value.TryGetProperty("forceBatch", out var forceBatchProp)
                && forceBatchProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                forceBatch = forceBatchProp.GetBoolean();
            }

            if (payload?.ValueKind == JsonValueKind.Object
                && payload.Value.TryGetProperty("platform", out var platformProp)
                && platformProp.ValueKind == JsonValueKind.String)
            {
                platform = platformProp.GetString() ?? "unknown";
            }

            if (payload?.ValueKind == JsonValueKind.Object
                && payload.Value.TryGetProperty("protocolVersion", out var protocolVersionProp)
                && protocolVersionProp.ValueKind == JsonValueKind.String)
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
            var (tokenAuthorized, tokenAuthFailureReason) = await IsSocketJoinTokenAuthorizedAsync(
                context,
                token,
                requestedSystemId,
                context.RequestAborted);

            if (!protocolSupported)
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    response: new SocketReasonResponse("unsupported_protocol_version"),
                    replyAsArrayFrame,
                        context.RequestAborted,
                        sendGate);
            }
            else if (isSystemTopic
                && string.Equals(payloadToken, token, StringComparison.Ordinal)
                && tokenAuthorized)
            {
                if (!rateLimiter.Allow(requestedSystemId))
                {
                    await SendPhoenixReplyAsync(
                        socket,
                        topic,
                        reference,
                        joinReference,
                        status: "error",
                        response: new SocketReasonResponse("rate_limited"),
                        replyAsArrayFrame,
                        context.RequestAborted,
                        sendGate);
                    continue;
                }

                joinedTopics[topic] = 0;
                joinedSystemId = requestedSystemId;
                topicReplyAsArrayFrame[topic] = replyAsArrayFrame;
                topicJoinReference[topic] = joinReference;

                // Start the per-socket event pump now that we know which system this socket is bound to.
                // The bus filter only delivers events whose TargetSystemId matches JoinedSystemId, so the
                // pump's ~38 subscriptions only see traffic for this user.
                //
                // Interlocked.CompareExchange flips pumpStarted from 0 to 1 atomically and returns the
                // previous value; only the thread that observed 0 actually constructs the push context
                // and starts the pump. A subsequent phx_join on the same socket simply observes 1 and
                // becomes a no-op rather than spinning a second pump / second SocketPushContext.
                if (Interlocked.CompareExchange(ref pumpStarted, 1, 0) == 0)
                {
                    var socketPushContext = new SocketPushContext(
                        socket,
                        joinedSystemId,
                        joinedTopics,
                        topicJoinReference,
                        topicReplyAsArrayFrame,
                        sendGate,
                        pushCts.Token,
                        requestOrigin: requestOrigin,
                        logger: logger);

                    socketPushTask = SocketEventPumpRunner.RunAllAsync(
                        eventBus,
                        socketPushContext,
                        frontingRepository,
                        alterRepository,
                        tagRepository,
                        settingsFieldRepository,
                        accountRepository,
                        friendshipRepository,
                        pollRepository,
                        journalRepository,
                        encryptionStateRepository);
                }

                var initPayload = await WebSocketInitialization.BuildJoinInitPayloadAsync(context, joinedSystemId, context.RequestAborted);
                var useBatchedInit = false;

                if (!isReconnect)
                {
                    var estimatedEncodedBytes = (int)(Encoding.UTF8.GetByteCount(WebSocketEvents.SerializeSocketJson(initPayload)) * 1.1);
                    useBatchedInit = forceBatch
                        || (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
                            && estimatedEncodedBytes > batchedInitThresholdBytes
                            && protocolVersion >= new Version(2, 0, 0));
                }

                object joinResponse;
                if (isReconnect)
                {
                    joinResponse = new SocketJoinReconnectPayload(initPayload.System);
                }
                else if (useBatchedInit)
                {
                    joinResponse = new SocketJoinBatchedPayload(
                        Batched: true,
                        System: initPayload.System,
                        Alters: null,
                        Fronts: null,
                        Tags: null);
                }
                else
                {
                    joinResponse = initPayload;
                }

                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "ok",
                    response: joinResponse,
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);

                if (useBatchedInit)
                {
                    await WebSocketInitialization.SendBatchedInitAsync(
                        socket,
                        topic,
                        topicJoinReference[topic],
                        topicReplyAsArrayFrame[topic],
                        initPayload,
                        context.RequestAborted,
                        sendGate);
                }
            }
            else
            {
                var unauthorizedReason = tokenAuthFailureReason ?? "unauthorized";
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: "error",
                    response: new SocketReasonResponse(unauthorizedReason),
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, unauthorizedReason, context.RequestAborted);
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
                    response: new SocketReasonResponse("not_joined"),
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);
                continue;
            }

            var endpointResult = await HandleEndpointProxyAsync(context, payload, token, joinedSystemId);

            await SendPhoenixReplyAsync(
                socket,
                topic,
                reference,
                joinReference,
                status: "ok",
                response: endpointResult,
                replyAsArrayFrame,
                context.RequestAborted,
                sendGate);
            continue;
        }

        //TODO: Add phx_leave
        await SendPhoenixReplyAsync(
            socket,
            topic,
            reference,
            joinReference,
            status: "error",
            response: new SocketReasonResponse("event_not_implemented"),
            replyAsArrayFrame,
            context.RequestAborted,
            sendGate);
    }

    await pushCts.CancelAsync();
    if (socketPushTask is not null)
    {
        try
        {
            await socketPushTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on socket shutdown.
        }
    }

    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
    {
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "socket closed",
            CancellationToken.None);
    }
}

static async Task<SocketEndpointProxyResponse> HandleEndpointProxyAsync(
    HttpContext websocketContext,
    JsonElement? payload,
    string socketToken,
    string? joinedSystemId)
{
    if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
    {
        return new SocketEndpointProxyResponse(
            StatusCodes.Status400BadRequest,
            ToJsonString(new ErrorResponse(
                "Invalid endpoint payload.",
                "socket_endpoint_payload_invalid",
                System.Net.HttpStatusCode.BadRequest)));
    }

    var payloadObj = payload.Value;
    var method = payloadObj.TryGetProperty("method", out var methodProp)
                 && methodProp.ValueKind == JsonValueKind.String
        ? methodProp.GetString() ?? string.Empty
        : string.Empty;

    var path = payloadObj.TryGetProperty("path", out var pathProp)
               && pathProp.ValueKind == JsonValueKind.String
        ? pathProp.GetString() ?? string.Empty
        : string.Empty;

    if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
    {
        return new SocketEndpointProxyResponse(
            StatusCodes.Status400BadRequest,
            ToJsonString(new ErrorResponse(
                "Endpoint payload must include method and path.",
                "socket_endpoint_method_path_required",
                System.Net.HttpStatusCode.BadRequest)));
    }

    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        return new SocketEndpointProxyResponse(
            StatusCodes.Status403Forbidden,
            ToJsonString(new ErrorResponse(
                "Socket endpoint relay is restricted to /api paths.",
                "socket_endpoint_path_forbidden",
                System.Net.HttpStatusCode.Forbidden)));
    }

    // Self-call the API's own Kestrel listener rather than dialing back through
    // `Request.Scheme`/`Request.Host`. In the published docker compose stack the
    // request arrives via the operator-facing hostname + host-mapped port (e.g.
    // `https://api.example.com:5001`), but neither is reachable from inside the
    // container: the hostname is rarely in the container's DNS namespace, and the
    // host-side port is the OUTSIDE of the compose port mapping (the container
    // itself listens on `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS`,
    // default 5100/5101, configurable via `Ports:api-container-http(s)`).
    // Resolving via IServerAddressesFeature picks up whatever Kestrel actually
    // bound to in this process, regardless of the deployment topology in front
    // of it. We prefer the HTTPS binding so the call doesn't trip
    // HttpsRedirection (which would 308 us at the HTTPS listener anyway), and
    // dial it through the named `LoopbackHttpClient` whose permissive TLS
    // validator accepts the local leaf cert despite the SAN/chain mismatches —
    // see `LoopbackHttpClient` for the safety argument.
    var server = websocketContext.RequestServices.GetRequiredService<IServer>();
    var baseUri = ResolveLoopbackBaseUri(server.Features.Get<IServerAddressesFeature>()?.Addresses);
    var targetUri = $"{baseUri}{path}";

    if (!Uri.TryCreate(targetUri, UriKind.Absolute, out var parsedTargetUri)
        || (parsedTargetUri.Scheme is "https" && !LoopbackHttpClient.IsLoopbackHost(parsedTargetUri)))
    {
        // Defence-in-depth: ResolveLoopbackBaseUri is unit-tested to always emit a loopback
        // shape (or the TestServer fallback `http://localhost`), but the named loopback
        // HttpClient skips TLS validation, so a future regression that let a non-loopback
        // host through here would silently weaken every self-call. Fail fast instead.
        return new SocketEndpointProxyResponse(
            StatusCodes.Status500InternalServerError,
            ToJsonString(new ErrorResponse(
                "Socket endpoint relay resolved a non-loopback target.",
                "socket_endpoint_proxy_misrouted",
                System.Net.HttpStatusCode.InternalServerError)));
    }

    using var request = new HttpRequestMessage(new HttpMethod(method), parsedTargetUri);
    // Forward the outer Host so the inner controller's `Request.Host` is the operator-facing
    // origin, not the loopback dial target — anything reading `Request.Host` for URL
    // qualification (`QualifyAvatar`, OAuth callbacks) would otherwise emit unreachable URLs.
    request.Headers.Host = websocketContext.Request.Host.Value;
    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {socketToken}");
    request.Headers.TryAddWithoutValidation("Accept", "application/json");

    if (!string.IsNullOrWhiteSpace(joinedSystemId))
    {
        request.Headers.TryAddWithoutValidation("X-Interfold-Principal", joinedSystemId);
    }

    if (payloadObj.TryGetProperty("body", out var bodyProp)
        && bodyProp.ValueKind != JsonValueKind.Null
        && method is not "GET" and not "HEAD")
    {
        //This NEEDS to be GetString() because the raw JSON is what we want to forward, not a re-serialized version of the body element.
        var requestBodyJson = bodyProp.GetString();
        if (requestBodyJson != null) {
            request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");
        }
    }

    var httpClientFactory = websocketContext.RequestServices.GetRequiredService<IHttpClientFactory>();
    using var httpClient = httpClientFactory.CreateClient(LoopbackHttpClient.Name);

    var response = await httpClient.SendAsync(request, websocketContext.RequestAborted);
    try
    {
        var responseBody = await response.Content.ReadAsStringAsync(websocketContext.RequestAborted);
        return new SocketEndpointProxyResponse((int)response.StatusCode, responseBody);
    }
    finally
    {
        response.Dispose();
    }
}

static string ToJsonString<T>(T value)
    => JsonSerializer.Serialize(value, SocketJson.Options);

/// <summary>
/// Picks a loopback-safe base URL from Kestrel's bound addresses for the socket
/// endpoint relay's self-call. Prefers <c>https://</c> over <c>http://</c> so the
/// self-call doesn't get 308-redirected by <c>UseHttpsRedirection</c> — we'd land on
/// the HTTPS listener either way, so dial it directly. The leaf PFX served by Kestrel
/// in self-host carries SANs for the operator-facing hostname (e.g.
/// <c>api.example.com</c>), not the loopback literal, and is signed by a private CA
/// not in the container's system trust store, so HTTPS loopback would fail TLS
/// validation under the default <see cref="HttpClient"/>; the call site routes
/// through <see cref="LoopbackHttpClient.Name"/>, whose permissive TLS validator is
/// safe because the dial target is always loopback and therefore can't be MITM'd from
/// outside the process.
///
/// <para>
/// Wildcard hosts (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c>, <c>*</c>) are rewritten to
/// <c>127.0.0.1</c> so the URL dials the local listener instead of trying to resolve
/// a wildcard literal. Falls back to <c>http://localhost</c> when no bound addresses
/// are reported — that branch is unreachable in production (Kestrel always reports
/// its actual bindings) but keeps the proxy functional under
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c>, whose no-op
/// <see cref="IServerAddressesFeature"/> exposes an empty address list because
/// requests are routed in-memory rather than over a real socket.
/// </para>
/// </summary>
internal static string ResolveLoopbackBaseUri(ICollection<string>? addresses)
{
    const string testServerFallback = "http://localhost";
    if (addresses is null || addresses.Count == 0) return testServerFallback;

    var preferred = addresses
        .OrderBy(static addr => addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault();
    if (preferred is null) return testServerFallback;

    return preferred.TrimEnd('/')
        .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
        .Replace("://[::]",    "://127.0.0.1", StringComparison.Ordinal)
        .Replace("://+",       "://127.0.0.1", StringComparison.Ordinal)
        .Replace("://*",       "://127.0.0.1", StringComparison.Ordinal);
}

static async Task<(bool IsAuthorized, string? FailureReason)> IsSocketJoinTokenAuthorizedAsync(
    HttpContext context,
    string token,
    string requestedSystemId,
    CancellationToken cancellationToken)
{
    var authConfig = context.RequestServices
        .GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>().CurrentValue;

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("WebSocketTokenAuth");

    if (string.IsNullOrWhiteSpace(token))
    {
        logger.LogWarning("Token is empty or whitespace");
        return (false, "missing_socket_token");
    }

    logger.LogInformation("Validating token. RequestedSystemId: {SystemId}", requestedSystemId);
    
    var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    if (!handler.CanReadToken(token))
    {
        logger.LogWarning("Handler cannot read token");
        return (false, "invalid_socket_token");
    }

    logger.LogInformation("Token is readable. Verification key count: {KeyCount}", 
        authConfig.JwtEs256VerificationKeyPems?.Length ?? 0);

    var parameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidAudience = authConfig.JwtAudience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        ValidateIssuerSigningKey = false,
        RequireSignedTokens = true,
        SignatureValidator = (socketToken, validationParameters) =>
            ValidateJwtTokenSignatureForSocket(socketToken, validationParameters, authConfig),
        NameClaimType = "sub"
    };

    try
    {
        logger.LogInformation("Starting token validation");
        var principal = handler.ValidateToken(token, parameters, out _);
        var tokenSystemId = principal.FindFirstValue("sub");
        
        logger.LogInformation("Token validated. TokenSystemId: {TokenSub}, RequestedSystemId: {RequestedSub}",
            tokenSystemId, requestedSystemId);
            
        if (string.IsNullOrWhiteSpace(tokenSystemId))
        {
            logger.LogWarning("Token subject (sub) claim is missing or empty");
            return (false, "invalid_socket_token_subject");
        }

        if (!string.Equals(tokenSystemId, requestedSystemId, StringComparison.Ordinal))
        {
            logger.LogWarning("Token subject does not match requested system ID");
            return (false, "unauthorized_topic");
        }

            // Check if token has been revoked
            var jti = principal.FindFirstValue("jti");
            if (!string.IsNullOrWhiteSpace(jti))
            {
                var revocationRepository = context.RequestServices
                    .GetRequiredService<IAuthTokenRevocationRepository>();
                var isTokenValid = await revocationRepository.ValidateTokenNotRevokedAsync(jti, cancellationToken);
                if (!isTokenValid)
                {
                    logger.LogWarning("Token has been revoked. JTI: {Jti}", jti);
                    return (false, "token_revoked");
                }
            }

            logger.LogInformation("Token authorization successful");
            return (true, null);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "WebSocket token validation failed: {ExceptionMessage}", ex.Message);
        return (false, "invalid_socket_token");
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

static SecurityToken ValidateJwtTokenSignatureForSocket(
    string token,
    TokenValidationParameters validationParameters,
    AuthenticationConfiguration config)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        throw new SecurityTokenInvalidSignatureException("Token is empty.");
    }

    var parts = token.Split('.');
    if (parts.Length != 3)
    {
        throw new SecurityTokenInvalidSignatureException($"Token has {parts.Length} segments, expected 3.");
    }

    string headerJson;
    try
    {
        headerJson = Encoding.UTF8.GetString(parts[0].Base64UrlDecode());
    }
    catch (Exception ex)
    {
        throw new SecurityTokenInvalidSignatureException($"Failed to decode header: {ex.Message}");
    }

    var alg = string.Empty;
    using (var headerDoc = JsonDocument.Parse(headerJson))
    {
        if (!headerDoc.RootElement.TryGetProperty("alg", out var algProp)
            || string.IsNullOrWhiteSpace(algProp.GetString()))
        {
            throw new SecurityTokenInvalidSignatureException("Missing JWT algorithm in header.");
        }

        alg = algProp.GetString()!;
    }

    var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
    byte[] signatureBytes;
    try
    {
        signatureBytes = parts[2].Base64UrlDecode();
    }
    catch (Exception ex)
    {
        throw new SecurityTokenInvalidSignatureException($"Failed to decode signature: {ex.Message}");
    }

    // ES256 (ECDSA P-256 with SHA-256) validation
    if (!string.Equals(alg, "ES256", StringComparison.Ordinal))
    {
        throw new SecurityTokenInvalidSignatureException($"Algorithm '{alg}' is not supported. Only ES256 is accepted.");
    }

    var pems = config.JwtEs256VerificationKeyPems ?? [];
    if (pems.Length == 0)
    {
        throw new SecurityTokenInvalidSignatureException("No ES256 verification keys are configured.");
    }

    int attemptCount = 0;
    foreach (var rawPem in pems)
    {
        attemptCount++;
        using var ecdsa = ECDsa.Create();
        try
        {
            var normalizedPem = NormalizePem(rawPem);
            ecdsa.ImportFromPem(normalizedPem.AsSpan());
        }
        catch (CryptographicException)
        {
            // Key import failed, try next key
            continue;
        }

        try
        {
            if (ecdsa.VerifyData(
                signingInput,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return new JwtSecurityToken(token);
            }
        }
        catch (CryptographicException)
        {
            // Verification failed with this key, try next
            continue;
        }
    }

    throw new SecurityTokenInvalidSignatureException(
        $"Invalid JWT signature: tested {attemptCount} verification key(s) but none matched.");
}

static string NormalizePem(string pem)
{
    if (string.IsNullOrWhiteSpace(pem))
        return pem;

    // Normalize various line ending formats to actual newlines
    var normalized = pem
        .Replace(@"\r\n", "\n", StringComparison.Ordinal)  // Escaped Windows (\r\n became \\r\\n)
        .Replace("\\r", "\n", StringComparison.Ordinal)     // Escaped carriage return
        .Replace("\\n", "\n", StringComparison.Ordinal)     // Escaped newline
        .Replace("\r\n", "\n", StringComparison.Ordinal)    // Windows line endings
        .Replace("\r", "\n", StringComparison.Ordinal);     // Old Mac line endings

    return normalized;
}

static bool TryParsePhoenixFrame(
    string frame,
    out string eventName,
    out string topic,
    out JsonElement? payload,
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

    if (trimmed.StartsWith('['))
    {
        try
        {
            using var arrayDoc = JsonDocument.Parse(frame);
            var root = arrayDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 5)
            {
                return false;
            }

            var joinRefElement = root[0];
            var refElement = root[1];
            var topicElement = root[2];
            var eventElement = root[3];

            if (topicElement.ValueKind != JsonValueKind.String
                || eventElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            // Preserve JSON null so replies can mirror it back as null (not "").
            joinReference = joinRefElement.ValueKind == JsonValueKind.String
                ? joinRefElement.GetString()
                : null;

            reference = refElement.ValueKind == JsonValueKind.String
                ? refElement.GetString()
                : null;

            topic = topicElement.GetString() ?? topic;
            eventName = eventElement.GetString() ?? string.Empty;
            payload = root[4].Clone();
            replyAsArrayFrame = true;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    try
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("event", out var eventProp)
            || eventProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        eventName = eventProp.GetString() ?? string.Empty;

        if (root.TryGetProperty("topic", out var topicProp)
            && topicProp.ValueKind == JsonValueKind.String)
        {
            topic = topicProp.GetString() ?? topic;
        }

        if (root.TryGetProperty("payload", out var payloadProp))
        {
            payload = payloadProp.Clone();
        }

        if (root.TryGetProperty("ref", out var refProp)
            && refProp.ValueKind == JsonValueKind.String)
        {
            reference = refProp.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("join_ref", out var joinRefProp)
            && joinRefProp.ValueKind == JsonValueKind.String)
        {
            joinReference = joinRefProp.GetString() ?? string.Empty;
        }

        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

 static async Task SendPhoenixReplyAsync<TResponse>(
     WebSocket socket,
     string topic,
     string? reference,
     string? joinReference,
     string status,
     TResponse response,
     bool replyAsArrayFrame,
     CancellationToken cancellationToken,
     SemaphoreSlim? sendGate = null)
 {
     var payload = new PhoenixReplyPayload<TResponse>(status, response);
     var bytes = replyAsArrayFrame
         ? PhxArrayFrame.CreateBytes(joinReference, reference, topic, "phx_reply", payload)
         : new PhxFrame<PhoenixReplyPayload<TResponse>>
         {
             Topic = topic,
             Event = "phx_reply",
             Payload = payload,
             Ref = reference,
             JoinRef = joinReference
         }.ToBytes();

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
}