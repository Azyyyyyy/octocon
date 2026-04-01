namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// OpenTelemetry and observability configuration for traces and metrics export.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class ObservabilityConfiguration
{
    public const string SectionName = "Octocon:Observability";

    /// <summary>
    /// OpenTelemetry Protocol (OTLP) gRPC endpoint for trace and metrics export.
    /// When set, enables export; when null/empty, metrics remain in-process only.
    /// Example: 'http://localhost:4317'
    /// Env: OCTOCON_OTLP_ENDPOINT
    /// </summary>
    public string? OtlpEndpoint { get; set; }
}
