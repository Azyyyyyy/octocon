using Interfold.Contracts.Operations;

namespace Interfold.Domain.Abstractions;

public interface ICommandHandler<TPayload, TResult>
{
    Task<CommandExecutionResult<TResult>> HandleAsync(
        CommandEnvelope<TPayload> command,
        CancellationToken cancellationToken = default
    );
}

public sealed class CommandExecutionResult<TResult>
{
    private CommandExecutionResult(TResult? result, ConflictResult? conflict, bool accepted)
    {
        Result = result;
        Conflict = conflict;
        Accepted = accepted;
    }

    public TResult? Result { get; }
    public ConflictResult? Conflict { get; }
    public bool Accepted { get; }

    public static CommandExecutionResult<TResult> Success(TResult result) => new(result, null, true);
    public static CommandExecutionResult<TResult> Rejected(ConflictResult conflict) => new(default, conflict, false);
}
