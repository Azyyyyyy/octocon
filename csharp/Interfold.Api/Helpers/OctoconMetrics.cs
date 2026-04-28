using System.Diagnostics.Metrics;

namespace Interfold.Api.Helpers;

/// <summary>
/// Application-level named <see cref="Meter"/> with the counters and histograms used
/// for Phase N observability hardening.
/// Wire via <c>builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Interfold.Api"))</c>.
/// </summary>
public static class InterfoldMetrics
{
    public static readonly string MeterName = "Interfold.Api";

    private static readonly Meter Meter = new(MeterName, "1.0");

    /// <summary>
    /// Total command invocations.  Tags: <c>operation_id</c>, <c>outcome</c> (accepted|replay|rejected).
    /// </summary>
    public static readonly Counter<long> CommandsTotal = Meter.CreateCounter<long>(
        "octocon.commands.total",
        description: "Total command invocations tagged by operation_id and outcome.");

    /// <summary>
    /// Command conflicts.  Tags: <c>operation_id</c>, <c>conflict_code</c>.
    /// </summary>
    public static readonly Counter<long> ConflictsTotal = Meter.CreateCounter<long>(
        "octocon.commands.conflicts.total",
        description: "Command conflict counter tagged by operation_id and conflict_code.");

    /// <summary>
    /// Command handler wall-clock latency in milliseconds.  Tags: <c>operation_id</c>.
    /// </summary>
    public static readonly Histogram<double> CommandLatencyMs = Meter.CreateHistogram<double>(
        "octocon.commands.latency_ms",
        unit: "ms",
        description: "Command handler latency in milliseconds.");
}
