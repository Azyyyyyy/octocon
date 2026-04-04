namespace Interfold.Domain.Friendships;

internal static class FriendshipIdNormalization
{
    public static string CanonicalizeForPrincipal(string principalSystemId, string candidateSystemId)
    {
        if (string.IsNullOrWhiteSpace(candidateSystemId))
        {
            return candidateSystemId;
        }

        var separatorIndex = candidateSystemId.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            return candidateSystemId[(separatorIndex + 1)..];
        }

        separatorIndex = principalSystemId.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return candidateSystemId;
        }

        var keyspace = principalSystemId[..separatorIndex];
        return $"{keyspace}:{candidateSystemId}";
    }
}