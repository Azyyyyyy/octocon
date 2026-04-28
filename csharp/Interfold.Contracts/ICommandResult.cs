namespace Interfold.Contracts;

/// <summary>
/// Marker interface for all command result records that carry a replay flag.
/// Used by the API layer to record metrics and structured log outcomes uniformly.
/// </summary>
public interface ICommandResult
{
    bool Replay { get; }
}