using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Text;
using Interfold.Api;
using Interfold.Api.Auth;
using Interfold.Api.Services;
using Interfold.Api.Swagger;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Persistence;
using System.Text.Json;
using Interfold.Api.Socket;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Bind startup-time values read before the DI container is built.
var clusterConfig     = builder.Configuration.BindClusterConfiguration();
var persistenceConfig = builder.Configuration.BindPersistenceConfiguration();
var obsConfig         = builder.Configuration.BindObservabilityConfiguration();
var authConfig        = builder.Configuration.BindAuthenticationConfiguration();

// Register all typed options
// Auth and API configs are consumed directly via IOptionsMonitor in services.
// Storage and socket configs are consumed via IOptionsMonitor only — no startup local needed.
builder.Services.AddInterfoldOptions();

// --- Node role ---
builder.Services.AddInterfoldCluster(NodeGroupResolver.Resolve(clusterConfig.NodeGroup));

// --- Persistence ---
var persistenceMode = persistenceConfig.Mode switch
{
    "inmemory"        => PersistenceMode.InMemory,
    "scylla-postgres" => PersistenceMode.ScyllaPostgres,
    var x             => throw new InvalidOperationException($"Unsupported persistence mode: {x}")
};

builder.Services.AddInterfoldPersistence(persistenceMode, persistenceConfig);
builder.Services.AddInterfoldDomainHandlers();
builder.Services.AddSingleton<IAvatarStorage, LocalAvatarStorage>();

// --- HTTP Client Factory (for OAuth token exchange, etc.) ---
builder.Services.AddHttpClient<GoogleOAuthService>();

// --- Auth ---
// Tokens are always self-issued by this backend after
// OAuth provider authentication completes. There is no single external OIDC authority to
// validate against — the provider is only used to identify the user;
// the resulting JWT is issued by Interfold itself. We therefore skip issuer validation and
// rely on audience and lifetime checks only for now. TODO: Look into how we can make this better WITHOUT breaking exisitng clients
var jwtAudience = "octocon";
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
            IssuerSigningKeyResolver = ResolveJwtSigningKeys,
            SignatureValidator = ValidateHs256TokenSignatureForBearer
        };
    });

// Register OAuth challenge schemes if authentication is enabled.  
// These are registered once at startup and remain static; only the parameters in
// IOptionsMonitor<AuthenticationConfiguration> are live-reloadable per request.
// OAuth challenge schemes are registered once at startup; only the parameters in
// IOptionsMonitor<AuthenticationConfiguration> are live-reloadable per request.
TryAddRedirectChallengeScheme(
    builder.Services,
    authConfig.DiscordSchemeName,
    authConfig.DiscordEndpoint,
    authConfig.DiscordParameters);

TryAddRedirectChallengeScheme(
    builder.Services,
    authConfig.GoogleSchemeName,
    authConfig.GoogleEndpoint,
    authConfig.GoogleParameters);

TryAddRedirectChallengeScheme(
    builder.Services,
    authConfig.AppleSchemeName,
    authConfig.AppleEndpoint,
    authConfig.AppleParameters);

builder.Services.AddAuthorization() //Builder
        /*.SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build())*/;

// --- OpenTelemetry (Phase N) ---
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

        if (!string.IsNullOrWhiteSpace(obsConfig.OtlpEndpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(obsConfig.OtlpEndpoint));
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();

        if (!string.IsNullOrWhiteSpace(obsConfig.OtlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(obsConfig.OtlpEndpoint));
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

var app = builder.Build();

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

app.UseAuthentication();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthorization();
app.Map("/api/socket", socketApp => socketApp.Run(WebSocketHandler.HandleUserSocketAsync));
app.MapControllers();

app.Run();
return 0;

SecurityKey[] ResolveJwtSigningKeys(
    string token,
    SecurityToken? securityToken,
    string? kid,
    TokenValidationParameters validationParameters)
    => (builder.Configuration.BindAuthenticationConfiguration().JwtSigningSecrets ?? [])
        .Select(static s => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(s)))
        .ToArray();

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

    // Prefer the live key resolver (reads from IConfiguration per call) over the static key list.
    var keys = (validationParameters.IssuerSigningKeyResolver != null
        ? validationParameters.IssuerSigningKeyResolver(token, null, null, validationParameters)
        : validationParameters.IssuerSigningKeys)
        ?.OfType<SymmetricSecurityKey>()
        .ToArray()
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