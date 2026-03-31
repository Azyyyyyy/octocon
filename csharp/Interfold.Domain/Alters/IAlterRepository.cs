using System.Text.Json.Serialization;

namespace Interfold.Domain.Alters;

public sealed record AlterPublicFieldReadModel(string Id, string Name, string Type, string? Value);

public class BareAlter {
    public BareAlter(
        int id,
        string name,
        string? avatarUrl,
        string? color,
        string? pronouns,
        string? description,
        IReadOnlyList<AlterPublicFieldReadModel> fields)
    {
        Id = id;
        Name = name;
        AvatarUrl = avatarUrl;
        Color = color;
        Pronouns = pronouns;
        Fields = fields;
        Description = description;
    }

    public int Id { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Color { get; set; }
    public string Name { get; set; }
    public string? Pronouns { get; set; }
    public IReadOnlyList<AlterPublicFieldReadModel> Fields { get; set; }
    public string? Description { get; set; }
 }

public sealed class AlterReadModel : BareAlter {

    public AlterReadModel(
        int id,
        string name,
        string? description,
        string? avatarUrl,
        string? color,
        string? pronouns,
        VisibilityLevel securityLevel,
        IReadOnlyList<AlterPublicFieldReadModel> fields,
        string? proxyName,
        string? alias,
        bool? untracked,
        bool? archived,
        bool? pinned) : base(id, name, avatarUrl, color, pronouns, description, fields)
    {
        Alias = alias;
        SecurityLevel = securityLevel;
        ProxyName = proxyName;
        Untracked = untracked ?? false;
        Archived = archived ?? false;
        Pinned = pinned ?? false;
    }

    public string? Alias { get; set; }
    public VisibilityLevel SecurityLevel { get; set; }
    public string? ProxyName { get; set; }
    public bool Untracked { get; set; }
    public bool Archived { get; set; }
    public bool Pinned { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisibilityLevel
{
    [JsonStringEnumMemberName("public")]
    Public = 0,
    [JsonStringEnumMemberName("friends_only")]
    FriendsOnly = 1,
    [JsonStringEnumMemberName("trusted_only")]
    TrustedOnly = 2,
    [JsonStringEnumMemberName("private")]
    Private = 3
}

public interface IAlterRepository
{
    Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<AlterReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<BareAlter?> GetGuardedAsync(
        string systemId,
        int alterId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    );
}