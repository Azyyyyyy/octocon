namespace Interfold.Contracts.Secrets;

public sealed record SecretEntry(
    string Key,
    string Value,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    string? RotatedFrom);

public interface ISecretsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<string> GetRequiredAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default);
}
