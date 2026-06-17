namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Minimal logging surface for the database seed orchestrators. The bootstrapper wraps its
/// existing <c>PhaseLogger</c> (Cli/PhaseLogger.cs) behind this interface so the shared
/// project doesn't pick up any concrete logging framework; tests can plug
/// <see cref="NoOpDatabaseInitLogger"/> when they don't care about the running commentary.
/// </summary>
public interface IDatabaseInitLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>
/// Drops every log message. Useful for tests where seed progress is implied by the
/// downstream assertions (and where TUnit's stdout capture would clutter the report).
/// </summary>
public sealed class NoOpDatabaseInitLogger : IDatabaseInitLogger
{
    public static readonly NoOpDatabaseInitLogger Instance = new();

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
