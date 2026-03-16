namespace Octocon.Domain.Settings;

public sealed record SettingsFieldReadModel(string FieldId, string Name, string? Value, int Position);

public interface ISettingsFieldRepository
{
    Task<string?> CreateAsync(string systemId, string name, string? value, int position, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(string systemId, string fieldId, string? name, string? value, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default);

    Task<bool> RelocateAsync(string systemId, string fieldId, int position, CancellationToken cancellationToken = default);
}
