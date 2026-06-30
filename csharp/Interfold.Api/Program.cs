using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api;
using Interfold.Api.Auth;
using Interfold.Api.Helpers;
using Interfold.Api.Middleware;
using Interfold.Api.Services;
using Interfold.Api.Services.Http;
using Interfold.Api.Socket;
using Interfold.Api.Swagger;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Aspire ServiceDefaults (OTel, resilience, service discovery) ---
builder.AddServiceDefaults();

// Self-host only: patch the leaf PFX password into IConfiguration before the host builds
// so Kestrel can unlock /certs/leaf.pfx at HTTPS-bind time. See LoadLeafPfxPasswordFromStoreIfNeeded
// below for the self-host trigger + ordering rationale.
LoadLeafPfxPasswordFromStoreIfNeeded(builder.Configuration);

//Database connections which have been implemented
ScyllaServiceCollectionExtensions.Register();
InMemoryServiceCollectionExtensions.Register();
PostgresServiceCollectionExtensions.Register();

// --- Configuration ---
// Register all typed options. consumed via IOptionsMonitor in services. 
// or by their registration helpers below.
IOptionsMonitor<AuthenticationConfiguration>? authOptionsMonitor = null;
var authConfig = builder.Configuration.BindAuthenticationConfiguration();
var persistenceConfig = builder.Configuration.BindPersistenceConfiguration();
builder.Services.AddInterfoldOptions();

// Comma-separated allow-list from OCTOCON_CORS_ALLOWED_ORIGINS; blank falls back to
// allow-any (dev-only — production stacks must set it explicitly). Trailing slashes
// trimmed for parity with the ASP.NET Core CORS matcher.
var configuredCorsOrigins = (builder.Configuration["OCTOCON_CORS_ALLOWED_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(static origin => origin.TrimEnd('/'))
    .Where(static origin => !string.IsNullOrWhiteSpace(origin))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (configuredCorsOrigins.Length > 0)
        {
            policy.WithOrigins(configuredCorsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
            return;
        }

        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Registered BEFORE persistence services so its StartingAsync runs before migration services
// try to read admin creds / OAuth secrets out of IConfiguration.
builder.Services.AddHostedService<SecretsBootstrapService>();

// --- Dependency Injection ---
builder.Services.AddInterfoldCluster(builder.Configuration);
builder.Services.AddInterfoldPersistence(builder.Configuration);
builder.Services.AddInterfoldDomainHandlers();

// --- Health Checks ---
// Readiness checks use a short timeout (5s) — fail fast if a dependency drops.
// Startup checks use a longer timeout (30s) — databases may still be initializing at boot.
var healthChecks = builder.Services.AddHealthChecks();

if (persistenceConfig.Mode == "scylla-postgres")
{
    healthChecks.AddCheck<Interfold.Infrastructure.Scylla.ScyllaHealthChecker>(
        "scylla-ready", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
    healthChecks.AddCheck<Interfold.Infrastructure.Scylla.ScyllaHealthChecker>(
        "scylla-startup", tags: ["startup"], timeout: TimeSpan.FromSeconds(30));
    healthChecks.AddCheck<Interfold.Infrastructure.Postgres.PostgresHealthChecker>(
        "postgres-ready", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
    healthChecks.AddCheck<Interfold.Infrastructure.Postgres.PostgresHealthChecker>(
        "postgres-startup", tags: ["startup"], timeout: TimeSpan.FromSeconds(30));
}
builder.Services.AddSingleton<IAvatarStorage, LocalAvatarStorage>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Interfold.Api.Socket.SocketJoinRateLimiter>();

builder.Services.AddTransient<HttpLoggingHandler>();

// --- HTTP Client Factory (for OAuth token exchange, etc.) ---
builder.Services.AddHttpClient<GoogleOAuthService>();
builder.Services.AddHttpClient<DiscordOAuthService>();
builder.Services.AddHttpClient<AppleOAuthService>();
builder.Services.AddHttpClient("SimplyPlural").AddHttpMessageHandler<HttpLoggingHandler>();
builder.Services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();

// Permissive-TLS named client for the WebSocket endpoint relay's self-call. The call
// site (WebSocketHandler.HandleEndpointProxyAsync + ResolveLoopbackBaseUri) guarantees
// a loopback destination; LoopbackHttpClient's XML doc covers why permissive validation
// is the right call there. AllowAutoRedirect off because the relay targets the HTTPS
// listener directly, so any redirect would be a bug to surface, not follow.
builder.Services.AddHttpClient(LoopbackHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    });

// --- Auth ---
// JWTs are self-issued post-OAuth (the provider only identifies the user); no external OIDC
// authority to validate iss against, so issuer validation is off and we rely on aud + lifetime.
// TODO: Look into how we can make this better WITHOUT breaking existing clients
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
            ValidAudience = authConfig.JwtAudience, //Has to be done at startup to wire into the JWT handler
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = false,
            RequireSignedTokens = true,
            SignatureValidator = (token, _) =>
                ValidateJwtTokenSignatureForBearer(
                    token,
                    authOptionsMonitor?.CurrentValue ?? authConfig)
        };
        // JTI revocation check is wired after app.Build() to access IAuthTokenRevocationRepository
    });

// OAuth challenge schemes are registered once at startup; only the parameters in
// IOptionsMonitor<AuthenticationConfiguration> are live-reloadable per request.
builder.Services.AddInterfoldAuthChallengeSchemes(builder.Configuration);

builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());

builder.Services.AddHsts(options =>
{
    options.ExcludedHosts.Add("api.octocon.app");
});

builder.Services.AddHttpsRedirection(options => 
{
    options.RedirectStatusCode = 308; // Permanent Redirect
});

// --- OpenTelemetry ---
// The InterfoldMetrics custom meter is registered on top of ServiceDefaults OTel config.
builder.Services
    .AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(InterfoldMetrics.MeterName);
    });

// --- MVC ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        // Ensure DateTime / DateTimeOffset are consistently emitted as UTC (single trailing 'Z')
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
    });

// --- Swagger/OpenAPI ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Interfold API",
        Version = "v1",
        Description = "Interfold API - Contract Version: 2026-03-v1"
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

        if (allowedConflicts.All(allowed => route?.Contains(allowed) != true))
        {
            var actionNames = string.Join(", ", apiDescriptions.Select(d => $"{d.ActionDescriptor.DisplayName}"));
            throw new NotSupportedException(
                $"Conflicting actions detected for route '{route}': {actionNames}. " +
                "If this is intentional, add the route to the allowedConflicts list in Program.cs.");
        }

        return first;
    });

    options.DocumentFilter<MultipleContentTypeOperationFilter>();
    options.DocumentFilter<HealthCheckDocumentFilter>();

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
});

builder.Services.AddExceptionHandler<ExceptionHandler>();

var app = builder.Build();

// Capture once after Build so we can log ES256 configuration details.
authOptionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AuthStartup");
var effectiveAuthConfig = authOptionsMonitor.CurrentValue;
var verificationKeyCount = effectiveAuthConfig.JwtEs256VerificationKeyPems?.Length ?? 0;
startupLogger.LogInformation(
    "ES256 token issuance is enabled. Verification key count: {VerificationKeyCount}.",
    verificationKeyCount);

app.UseExceptionHandler("/error");

// Buffer avatar multipart PUTs so source-validation can re-read Request.Body after MVC
// model-binds the form. Scoped to the two avatar routes to keep the cost off everything else.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPut(context.Request.Method)
        && context.Request.HasFormContentType)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.Equals("/api/settings/avatar", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/systems/me/alters/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/avatar", StringComparison.OrdinalIgnoreCase)))
        {
            context.Request.EnableBuffering();
        }
    }

    await next();
});

// JWT token revocation check middleware.
// This runs after authentication, checking if the authenticated token's JTI has been revoked.
app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated == true)
    {
        if (context.User.FindFirst("jti")?.Value is { } jti && !string.IsNullOrWhiteSpace(jti))
        {
            var revocationRepository = context.RequestServices.GetRequiredService<IAuthTokenRevocationRepository>();
            var isTokenValid = await revocationRepository.ValidateTokenNotRevokedAsync(jti, context.RequestAborted);
            
            if (!isTokenValid)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                var error = new { error = "Token has been revoked.", code = "token_revoked" };
                var json = JsonSerializer.Serialize(error);
                await context.Response.WriteAsync(json, context.RequestAborted);
                return;
            }
        }
    }

    await next();
});

// Phase N: correlation ID propagation and structured request logging.
app.UseMiddleware<RequestCorrelationMiddleware>();

// --- Swagger UI ---
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Interfold API v1");
    options.RoutePrefix = "swagger";
});

// X-Interfold-Contract response header on every response
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers["X-Interfold-Contract"] = "2026-03-v1";
        return Task.CompletedTask;
    });
    await next();
});

app.UseHsts();
// Carve /.well-known out of HTTPS-redirect so TrustController can serve the root CA over
// plain HTTP — clients can't trust HTTPS until they've fetched and installed that root.
app.UseWhen(
    static ctx => !ctx.Request.Path.StartsWithSegments("/.well-known"),
    static branch => branch.UseHttpsRedirection());
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<InterfoldPrincipalMiddleware>();
app.UseStaticFiles();

// Serve avatars per AvatarServingPolicy (see its XML doc for the config matrix). Hand-rolled
// rather than a secondary UseStaticFiles because the policy reads IOptionsMonitor per request
// — LocalAvatarStorage stamps URLs from the same monitor, so write-side and read-side must
// agree on current values across config reloads. Unknown extensions fall through (matches
// StaticFileMiddleware's ServeUnknownFileTypes=false); the upload controller already gates
// content-type at write time.
var avatarContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
app.Use(async (context, next) =>
{
    var storageMonitor = context.RequestServices.GetRequiredService<IOptionsMonitor<StorageConfiguration>>();
    var current = storageMonitor.CurrentValue;
    var (shouldServe, physicalRoot, requestPath) = AvatarServingPolicy.Resolve(
        current.AvatarStorageRoot, current.AvatarPublicBase);

    if (!shouldServe
        || !context.Request.Path.StartsWithSegments(requestPath, StringComparison.Ordinal, out var remainingPath)
        || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
    {
        await next();
        return;
    }

    var relative = remainingPath.Value?.TrimStart('/') ?? string.Empty;
    if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    var rootFull = Path.GetFullPath(physicalRoot);
    var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
        ? rootFull
        : rootFull + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(Path.Combine(rootFull, relative));
    if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (!File.Exists(fullPath))
    {
        await next();
        return;
    }

    if (!avatarContentTypeProvider.TryGetContentType(fullPath, out var contentType))
    {
        await next();
        return;
    }

    var fi = new FileInfo(fullPath);
    context.Response.ContentType = contentType;
    context.Response.ContentLength = fi.Length;
    // Safe to cache long-term: LocalAvatarStorage embeds a timestamp+Guid in the filename,
    // so any overwrite produces a fresh URL.
    context.Response.Headers.CacheControl = "public, max-age=86400";
    if (HttpMethods.IsHead(context.Request.Method))
    {
        return;
    }
    await context.Response.SendFileAsync(fullPath, context.RequestAborted);
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthorization();

// --- Health Check Endpoints (from ServiceDefaults) ---
app.MapDefaultEndpoints();

app.MapMethods("/api/socket/websocket", ["GET", "CONNECT"], WebSocketHandler.HandleUserSocketAsync).AllowAnonymous();
app.MapControllers();

app.Run();
return 0;

// ES256 JWT token validation using ECDSA P-256 public keys.
static SecurityToken ValidateJwtTokenSignatureForBearer(
    string token,
    AuthenticationConfiguration config)
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
    string alg;
    using (var headerDoc = JsonDocument.Parse(headerJson))
    {
        if (!headerDoc.RootElement.TryGetProperty("alg", out var algProp)
            || string.IsNullOrWhiteSpace(algProp.GetString()))
        {
            throw new SecurityTokenInvalidSignatureException("Missing JWT algorithm.");
        }

        alg = algProp.GetString()!;
    }

    var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
    var signatureBytes = parts[2].Base64UrlDecode();

    // ES256 (ECDSA P-256 with SHA-256) validation
    if (!string.Equals(alg, "ES256", StringComparison.Ordinal))
    {
        throw new SecurityTokenInvalidSignatureException("Only ES256 algorithm is supported.");
    }

    var pems = config.JwtEs256VerificationKeyPems ?? [];
    if (pems.Length == 0)
    {
        throw new SecurityTokenInvalidSignatureException("No ES256 verification keys configured.");
    }

    foreach (var rawPem in pems)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(NormalizePem(rawPem).AsSpan());
        }
        catch (CryptographicException)
        {
            continue;
        }

        if (ecdsa.VerifyData(
            signingInput,
            signatureBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return new JsonWebToken(token);
        }
    }

    throw new SecurityTokenInvalidSignatureException("Invalid JWT signature: no verification key matched.");
}

static string NormalizePem(string pem)
{
    if (string.IsNullOrWhiteSpace(pem))
        return pem;

    // PEMs arrive from env vars / DB with both real and escaped line endings; collapse all
    // of them to '\n' so ECDsa.ImportFromPem accepts the result.
    var normalized = pem
        .Replace(@"\r\n", "\n", StringComparison.Ordinal)
        .Replace("\\r", "\n", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);

    return normalized;
}

/// <summary>
/// Fetches <c>certs:leaf_pfx_password</c> from <c>internal.secrets</c> via a transient
/// Npgsql connection and writes it into <c>Kestrel:Certificates:Default:Password</c> so
/// Kestrel's built-in PFX loader can unlock the leaf cert at HTTPS-endpoint bind time.
///
/// Self-host only: triggered solely when the AppHost has injected a Kestrel default-cert
/// path AND a Postgres connection string. Local dev (which uses the default dotnet dev
/// cert) sees neither and the loader becomes a no-op.
/// </summary>
static void LoadLeafPfxPasswordFromStoreIfNeeded(IConfigurationBuilder cfg)
{
    var config = (IConfigurationRoot)((IConfigurationBuilder)cfg).Build();
    var pfxPath = config["Kestrel:Certificates:Default:Path"]
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
    if (string.IsNullOrWhiteSpace(pfxPath)) return;

    // If the operator pinned a password via env (the legacy path) prefer that over the
    // store lookup. Lets local dev or one-off recovery flows bypass the DB roundtrip.
    var existingPassword = config["Kestrel:Certificates:Default:Password"];
    if (!string.IsNullOrWhiteSpace(existingPassword)) return;

    var pgConn = config["OCTOCON_POSTGRES_CONNECTION"];
    if (string.IsNullOrWhiteSpace(pgConn))
    {
        throw new InvalidOperationException(
            "Kestrel default-cert path is set but OCTOCON_POSTGRES_CONNECTION is missing; " +
            "cannot fetch certs:leaf_pfx_password from internal.secrets.");
    }

    string? password;
    try
    {
        using var conn = new NpgsqlConnection(pgConn);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT value FROM internal.secrets WHERE key = 'certs:leaf_pfx_password' LIMIT 1",
            conn);
        password = cmd.ExecuteScalar() as string;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            "Failed to fetch certs:leaf_pfx_password from internal.secrets. Ensure Postgres " +
            "is reachable at startup and that DatabaseInitPhase has seeded the row.", ex);
    }

    if (string.IsNullOrEmpty(password))
    {
        throw new InvalidOperationException(
            "Row internal.secrets[certs:leaf_pfx_password] is missing or empty; " +
            "re-run the bootstrapper so SecretsPhase + DatabaseInitPhase seed it.");
    }

    cfg.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Kestrel:Certificates:Default:Password"] = password,
    });
}