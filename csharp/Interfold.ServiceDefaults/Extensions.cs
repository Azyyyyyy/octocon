using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler().Configure(ConfigureResilienceForGetOnly);
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Gates the standard resilience pipeline (Retry + CircuitBreaker) so it only fires for
    /// idempotent GET requests, and tightens the timeout strategies to the configured
    /// ceilings (30 s per attempt, 2 min total).
    ///
    /// <para>
    /// <b>Why GET-only.</b> The default <see cref="HttpStandardResilienceOptions"/> retries
    /// on any transient outcome including <c>TimeoutRejectedException</c>. For state-changing
    /// verbs (POST/PUT/PATCH/DELETE) a retry after the per-attempt timeout fires is
    /// indistinguishable, from the server's perspective, from a brand-new call — the original
    /// request may have already started or finished. The Pi 4 dump captured in
    /// <c>scripts/diagnostics/raspi_dump.txt</c> (Polly[0] OnTimeout at the 10 s default,
    /// followed by two more full import runs) is the canonical witness. GETs are safe to
    /// retry; everything else gets a one-shot pass through the pipeline.
    /// </para>
    ///
    /// <para>
    /// <b>Why we still set the timeouts.</b> Even for non-GETs the AttemptTimeout and
    /// TotalRequestTimeout strategies still apply (the strategies themselves have no
    /// <c>ShouldHandle</c> hook). Setting AttemptTimeout = 30 s and TotalRequestTimeout =
    /// 2 min gives long-running mutations like the SP import enough room to complete without
    /// being cancelled mid-flight, while keeping a hard ceiling so genuinely stuck requests
    /// can't hold a connection forever. CircuitBreaker.SamplingDuration is bumped to 60 s to
    /// satisfy the framework constraint (≥ 2 × AttemptTimeout).
    /// </para>
    /// </summary>
    public static void ConfigureResilienceForGetOnly(HttpStandardResilienceOptions options)
    {
        var defaultRetryShould = options.Retry.ShouldHandle;
        options.Retry.ShouldHandle = args =>
        {
            var request = args.Context.GetRequestMessage();
            if (request is null || request.Method != HttpMethod.Get)
            {
                return new ValueTask<bool>(false);
            }
            return defaultRetryShould(args);
        };

        var defaultBreakerShould = options.CircuitBreaker.ShouldHandle;
        options.CircuitBreaker.ShouldHandle = args =>
        {
            var request = args.Context.GetRequestMessage();
            if (request is null || request.Method != HttpMethod.Get)
            {
                return new ValueTask<bool>(false);
            }
            return defaultBreakerShould(args);
        };

        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(1);
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health")
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Liveness: no dependency checks
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous().ShortCircuit();

        // Readiness: checks tagged "ready"
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        }).AllowAnonymous().ShortCircuit();

        // Startup: checks tagged "startup" (longer timeout for DB init)
        app.MapHealthChecks("/health/startup", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("startup")
        }).AllowAnonymous().ShortCircuit();

        return app;
    }
}
