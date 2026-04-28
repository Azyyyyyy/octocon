namespace Interfold.Contracts;

/// <summary>Well-known singleton task name constants.</summary>
public static class SingletonTaskNames
{
    /// <summary>Batched push-notification flush for fronting changes.</summary>
    public const string FrontNotifier = "front_notifier";

    /// <summary>Single-writer link-token issuer.</summary>
    public const string LinkTokenRegistry = "link_token_registry";
}