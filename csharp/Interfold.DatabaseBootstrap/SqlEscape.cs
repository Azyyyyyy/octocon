namespace Interfold.DatabaseBootstrap;

/// <summary>
/// SQL / CQL single-quote literal escaping. Both Postgres and Cassandra/Scylla use the same
/// rule: double up the apostrophe inside a quoted literal. The bootstrapper's password
/// alphabet excludes the apostrophe so this is conservative — we still escape on principle
/// in case a future operator pipes a custom value through.
/// </summary>
public static class SqlEscape
{
    /// <summary>Returns <paramref name="value"/> with any embedded apostrophes doubled.</summary>
    public static string Literal(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
