using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Auth;
using Interfold.Api.Helpers;
using Interfold.Api.Middleware;
using Interfold.Api.Services;
using Interfold.Api.Socket;
using Interfold.Api.Swagger;
using Interfold.Domain.Auth;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Register all typed options. consumed via IOptionsMonitor in services. 
// or by their registration helpers below.
IOptionsMonitor<AuthenticationConfiguration>? authOptionsMonitor = null;
var authConfig = builder.Configuration.BindAuthenticationConfiguration();
var persistenceConfig = builder.Configuration.BindPersistenceConfiguration();
builder.Services.AddInterfoldOptions();

//TODO: Add Cors:AllowedOrigins?
var configuredCorsOrigins = new[]
{
    builder.Configuration["OCTOCON_FRONTEND"],
    builder.Configuration["OCTOCON_BETA_FRONTEND"]
}
    .Where(static origin => !string.IsNullOrWhiteSpace(origin))
    .Select(static origin => origin!.Trim().TrimEnd('/'))
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

// --- Dependency Injection ---
builder.Services.AddInterfoldCluster(builder.Configuration);
builder.Services.AddInterfoldPersistence(builder.Configuration);
builder.Services.AddInterfoldDomainHandlers();
builder.Services.AddSingleton<IAvatarStorage, LocalAvatarStorage>();

// --- HTTP Client Factory (for OAuth token exchange, etc.) ---
builder.Services.AddHttpClient<GoogleOAuthService>();
builder.Services.AddHttpClient<DiscordOAuthService>();
builder.Services.AddHttpClient<AppleOAuthService>();

// --- Auth ---
// Tokens are always self-issued by this backend after
// OAuth provider authentication completes. There is no single external OIDC authority to
// validate against — the provider is only used to identify the user;
// the resulting JWT is issued by Interfold itself. We therefore skip issuer validation and
// rely on audience and lifetime checks only for now.
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
            SignatureValidator = (token, validationParameters) =>
                ValidateJwtTokenSignatureForBearer(
                    token,
                    validationParameters,
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
// Traces and metrics are exported via OTLP when OCTOCON_OTLP_ENDPOINT is set.
// Without the env var the SDK still runs in-process so metrics are always available
// for internal /metrics scraping or future export without code changes.
builder.Services
    .AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(InterfoldMetrics.MeterName);

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OCTOCON_OTLP_ENDPOINT"]))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["OCTOCON_OTLP_ENDPOINT"]!));
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OCTOCON_OTLP_ENDPOINT"]))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["OCTOCON_OTLP_ENDPOINT"]!));
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
startupLogger.LogInformation(
    "ES256 key file paths. PrivateKeyFile: {PrivateKeyFile}; PublicKeyFile: {PublicKeyFile}.",
    effectiveAuthConfig.JwtEs256PrivateKeyFile ?? "(not set)",
    effectiveAuthConfig.JwtEs256PublicKeyFile ?? "(not set)");

// Ensure avatar multipart uploads are buffered before any component touches Request.Body.
app.UseExceptionHandler("/error");

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

var persistenceStartupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PersistenceStartup");
persistenceStartupLogger.LogInformation(
    "Compatibility mode is {CompatibilityMode}. {Behavior}",
    persistenceConfig.CompatibilityMode,
    persistenceConfig.CompatibilityMode
        ? "Postgres idempotency and token revocation are using in-memory stores."
        : "Postgres idempotency and token revocation are using durable Postgres stores.");

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
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<InterfoldPrincipalMiddleware>();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthorization();
app.MapMethods("/api/socket/websocket", ["GET", "CONNECT"], WebSocketHandler.HandleUserSocketAsync).AllowAnonymous();
app.MapControllers();

app.Run();
return 0;

// ES256 JWT token validation using ECDSA P-256 public keys.
static SecurityToken ValidateJwtTokenSignatureForBearer(
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
        throw new SecurityTokenInvalidSignatureException("Token is not a valid JWS compact token.");
    }

    var headerJson = Encoding.UTF8.GetString(parts[0].Base64UrlDecode());
    var alg = string.Empty;
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

    // Normalize various line ending formats to actual newlines
    var normalized = pem
        .Replace(@"\r\n", "\n", StringComparison.Ordinal)  // Escaped Windows (\r\n became \\r\\n)
        .Replace("\\r", "\n", StringComparison.Ordinal)     // Escaped carriage return
        .Replace("\\n", "\n", StringComparison.Ordinal)     // Escaped newline
        .Replace("\r\n", "\n", StringComparison.Ordinal)    // Windows line endings
        .Replace("\r", "\n", StringComparison.Ordinal);     // Old Mac line endings

    return normalized;
}