using System.Text.Json.Serialization;

namespace Octocon.Domain.Alters;

public sealed record AlterPublicFieldReadModel(string Id, string Name, string Type, string? Value);

public sealed class AlterReadModel {

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
        bool? pinned)
    {
        Id = id;
        Name = name;
        Alias = alias;
        Fields = fields;
        SecurityLevel = securityLevel;
        ProxyName = proxyName;
        Description = description;
        AvatarUrl = avatarUrl;
        Color = color;
        Pronouns = pronouns;
        Untracked = untracked ?? false;
        Archived = archived ?? false;
        Pinned = pinned ?? false;
    }

    public int Id { get; set; }
    public string Name { get; set; }
    public string? Alias { get; set; }
    public IReadOnlyList<AlterPublicFieldReadModel>? Fields { get; set; }
    public VisibilityLevel SecurityLevel { get; set; }
    public string? ProxyName { get; set; }
    public string? Description { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Color { get; set; }
    public string? Pronouns { get; set; }
    public bool Untracked { get; set; }
    public bool Archived { get; set; }
    public bool Pinned { get; set; }
}

public sealed class AlterPublicReadModel {

    public AlterPublicReadModel(
        int id,
        string name,
        string? alias,
        IReadOnlyList<AlterPublicFieldReadModel>? fields = null,
        VisibilityLevel securityLevel = VisibilityLevel.Public)
    {
        Id = id;
        Name = name;
        Alias = alias;
        Fields = fields;
        SecurityLevel = securityLevel;
    }

    public int Id { get; set; }
    public string Name { get; set; }
    public string? Alias { get; set; }
    public IReadOnlyList<AlterPublicFieldReadModel>? Fields { get; set; }
    public VisibilityLevel SecurityLevel { get; set; }
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

    Task<IReadOnlyList<AlterPublicReadModel>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<AlterReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<AlterPublicReadModel?> GetGuardedAsync(
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