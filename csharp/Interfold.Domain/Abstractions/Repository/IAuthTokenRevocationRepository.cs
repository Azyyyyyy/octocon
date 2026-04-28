namespace Interfold.Domain.Abstractions.Repository;

/// <summary>
/// Contract for managing issued JWT token revocation.
/// 
/// Tokens are tracked by their JTI (JWT ID) claim to enable per-token revocation
/// without using refresh tokens. When a token is issued, it is recorded with an expiration time.
/// On validation, the system checks if the token has been explicitly revoked.
/// 
/// This supports the following scenarios:
/// - Logout: client calls POST /auth/revoke to invalidate their current token
/// - Security incident: admin can invalidate a specific token by JTI
/// - Cleanup: background job removes expired entries to prevent table bloat
/// </summary>
public interface IAuthTokenRevocationRepository
{
    /// <summary>
    /// Record a newly issued deep-link token for revocation tracking.
    /// </summary>
    /// <param name="jti">The JWT ID (jti) claim from the token being issued.</param>
    /// <param name="systemId">The system ID (subject) this token was issued to.</param>
    /// <param name="expiresAt">The UTC timestamp when this token expires naturally (from the exp claim).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when the token is recorded.</returns>
    Task RecordTokenAsync(
        string jti,
        string systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a token is valid (not revoked and not expired).
    /// </summary>
    /// <param name="jti">The JWT ID to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the token exists, is not revoked, and has not yet expired; false otherwise.</returns>
    /// <remarks>
    /// This method is called on every authenticated request during JWT validation.
    /// It should be fast (ideally < 1ms for a PK lookup on indexed JTI).
    /// </remarks>
    Task<bool> ValidateTokenNotRevokedAsync(
        string jti,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly revoke a token (logout, incident response, or admin action).
    /// </summary>
    /// <param name="jti">The JWT ID to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when the token is marked as revoked.</returns>
    /// <remarks>
    /// This is idempotent: revoking an already-revoked token is safe and returns success.
    /// </remarks>
    Task RevokeTokenAsync(
        string jti,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find all tokens issued to a specific system ID.
    /// Used for audit, bulk revocation, or session management.
    /// </summary>
    /// <param name="systemId">The system ID to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of JTI values for tokens issued to this system.</returns>
    Task<IReadOnlyList<string>> FindTokensBySystemIdAsync(
        string systemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up expired and revoked tokens from the table.
    /// Called periodically (e.g., hourly or daily) to prevent table bloat.
    /// </summary>
    /// <param name="olderThan">Optional: only delete entries where expires_at or revoked_at is older than this time.
    ///                         If null, defaults to current time (delete only already-expired entries).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> CleanupExpiredTokensAsync(
        DateTimeOffset? olderThan = null,
        CancellationToken cancellationToken = default);
}
