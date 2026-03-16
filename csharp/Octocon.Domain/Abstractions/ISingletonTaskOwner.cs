namespace Octocon.Domain.Abstractions;

/// <summary>
/// Owns (or conditionally gates) a named singleton background task.
/// <para>
/// Mirrors the legacy pattern where only <c>primary</c> nodes start
/// <c>Octocon.Global.FrontNotifier</c> and <c>Octocon.Global.LinkTokenRegistry</c>
/// via the Horde DynamicSupervisor.
/// </para>
/// </summary>
public interface ISingletonTaskOwner
{
    /// <summary>
    /// Returns <see langword="true"/> if this node should run the named singleton task.
    /// </summary>
    bool OwnsTask(string taskName);

    /// <summary>
    /// Runs <paramref name="work"/> if this node owns <paramref name="taskName"/>;
    /// otherwise returns <see cref="Task.CompletedTask"/> immediately.
    /// </summary>
    Task RunIfOwnerAsync(
        string taskName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}

/// <summary>Well-known singleton task name constants.</summary>
public static class SingletonTaskNames
{
    /// <summary>Batched push-notification flush for fronting changes.</summary>
    public const string FrontNotifier = "front_notifier";

    /// <summary>Single-writer link-token issuer.</summary>
    public const string LinkTokenRegistry = "link_token_registry";
}
