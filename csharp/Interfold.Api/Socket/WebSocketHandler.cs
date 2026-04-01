using System.Net.WebSockets;
using System.IdentityModel.Tokens.Jwt;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Fronting;
using Interfold.Domain.Alters;
using Interfold.Domain.Tags;
using Interfold.Domain.Settings;
using System.Text.Json;
using System.Text;
using Interfold.Domain.Accounts;
using Microsoft.IdentityModel.Tokens;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using System.Security.Claims;
using Interfold.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Socket;

public static class WebSocketHandler
{
public static async Task HandleUserSocketAsync(HttpContext context)
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
    var frontingRepository = context.RequestServices.GetRequiredService<IFrontingRepository>();
    var alterRepository = context.RequestServices.GetRequiredService<IAlterRepository>();
    var tagRepository = context.RequestServices.GetRequiredService<ITagRepository>();
    var settingsFieldRepository = context.RequestServices.GetRequiredService<ISettingsFieldRepository>();
    var accountRepository = context.RequestServices.GetRequiredService<IAccountRepository>();
    var pollRepository = context.RequestServices.GetRequiredService<IPollRepository>();
    var journalRepository = context.RequestServices.GetRequiredService<IJournalRepository>();
    var encryptionStateRepository = context.RequestServices.GetRequiredService<IEncryptionStateRepository>();
    var socketPushContext = new SocketPushContext(
        socket,
        joinedTopics,
        topicJoinReference,
        topicReplyAsArrayFrame,
        sendGate,
        pushCts.Token);

    var socketPushTask = SocketEventPumpRunner.RunAllAsync(
        eventBus,
        socketPushContext,
        frontingRepository,
        alterRepository,
        tagRepository,
        settingsFieldRepository,
        accountRepository,
        pollRepository,
        journalRepository,
        encryptionStateRepository);

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

                var initJson = await WebSocketInitialization.BuildJoinInitJsonAsync(context, joinedSystemId, context.RequestAborted);
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
                    await WebSocketInitialization.SendBatchedInitAsync(
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
                    responseJson: WebSocketEvents.SerializeSocketJson(new { reason = unauthorizedReason }),
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
            var endpointResponseJson = JsonSerializer.Serialize(endpointResult);

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
        await socketPushTask;
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
    JsonElement? payload,
    string socketToken,
    string? joinedSystemId)
{
    if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
    {
        return new
        {
            status = StatusCodes.Status400BadRequest,
            body = "{\"error\":\"Invalid endpoint payload.\",\"code\":\"socket_endpoint_payload_invalid\"}"
        };
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
    }

    string? requestBodyJson = null;
    if (payloadObj.TryGetProperty("body", out var bodyProp)
        && bodyProp.ValueKind != JsonValueKind.Null
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

    var jwtAudience = "octocon";
    var jwtSigningSecrets = context.RequestServices
        .GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>()
        .CurrentValue.JwtSigningSecrets ?? [];

    var jwtSigningKeys = jwtSigningSecrets
        .Select(static secret => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret!)))
        .ToArray();

    var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    if (!handler.CanReadToken(token))
    {
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
            return new JwtSecurityToken(token);
        }
    }

    throw new SecurityTokenInvalidSignatureException("Invalid JWT signature.");
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

    if (trimmed.StartsWith("[", StringComparison.Ordinal))
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
    var escapedTopic = JsonSerializer.Serialize(topic);
    // Preserve null so the client receives null rather than empty-string, which
    // Phoenix clients use to distinguish heartbeat replies from channel pushes.
    var escapedRef = reference is null ? "null" : JsonSerializer.Serialize(reference);
    var escapedJoinRef = joinReference is null ? "null" : JsonSerializer.Serialize(joinReference);
    var escapedStatus = JsonSerializer.Serialize(status);
    var escapedEvent = JsonSerializer.Serialize("phx_reply");

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
}