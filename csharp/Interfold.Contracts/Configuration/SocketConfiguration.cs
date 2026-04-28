namespace Interfold.Contracts.Configuration;

/// <summary>
/// WebSocket message batching and performance tuning.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class SocketConfiguration
{
    public const string SectionName = "Octocon:Socket";

    /// <summary>
    /// Threshold in bytes for batching outbound WebSocket messages.
    /// When the batched payload reaches this size, it's flushed immediately.
    /// Env: OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD
    /// Default: null (use built-in default)
    /// </summary>
    public int? BatchBytesThreshold { get; set; }
}
