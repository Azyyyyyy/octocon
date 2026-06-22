using System.Text.Json;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

/// <summary>
/// Marker interface for socket event payloads.
/// </summary>
public interface ISocketPayload { }

public sealed record EmptyPayload : ISocketPayload;

public sealed record PhoenixReplyPayload<TResponse>(string Status, TResponse Response) : ISocketPayload;

public sealed record SocketReasonResponse(string Reason) : ISocketPayload;

public sealed record SocketEndpointProxyResponse(int Status, string Body) : ISocketPayload;

public sealed record SocketJoinInitPayload(
    SocketSelfReadModel System,
    IReadOnlyList<AlterReadModel> Alters,
    IReadOnlyList<FrontActiveReadModel> Fronts,
    IReadOnlyList<TagReadModel> Tags) : ISocketPayload;

public sealed record SocketJoinReconnectPayload(SocketSelfReadModel System) : ISocketPayload;

public sealed record SocketJoinBatchedPayload(
    bool Batched,
    SocketSelfReadModel System,
    IReadOnlyList<AlterReadModel>? Alters,
    IReadOnlyList<FrontActiveReadModel>? Fronts,
    IReadOnlyList<TagReadModel>? Tags) : ISocketPayload;

public sealed record SocketBatchedAltersPayload(int BatchIndex, int TotalBatches, IReadOnlyList<AlterReadModel> Alters) : ISocketPayload;

public sealed record SocketBatchedTagsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<TagReadModel> Tags) : ISocketPayload;

public sealed record SocketBatchedFrontsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record SocketSelfReadModel(
    string Id,
    string? Username,
    string? Description,
    string? AvatarUrl,
    AvatarSource? AvatarSource,
    string? DiscordId,
    string? GoogleId,
    string? AppleId,
    string? Email,
    string AutoproxyMode,
    bool ShowSystemTag,
    int LifetimeAlterCount,
    int? PrimaryFront,
    IReadOnlyList<SettingsFieldReadModel> Fields,
    bool EncryptionInitialized) : ISocketPayload;

public sealed record AlterSocketPayload(AlterReadModel Alter) : ISocketPayload;

public sealed record AlterDeletedSocketPayload(int AlterId) : ISocketPayload;

public sealed record TagSocketPayload(TagReadModel Tag) : ISocketPayload;

public sealed record TagDeletedSocketPayload(string TagId) : ISocketPayload;

public sealed record PollSocketPayload(PollReadModel Poll) : ISocketPayload;

public sealed record PollDeletedSocketPayload(string PollId) : ISocketPayload;

public sealed record FrontSocketPayload(FrontActiveReadModel Front) : ISocketPayload;

public sealed record FrontsSocketPayload(IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record AlterIdSocketPayload(int? AlterId) : ISocketPayload;

public sealed record FrontIdSocketPayload(string FrontId) : ISocketPayload;

public sealed record GlobalJournalSocketPayload(JournalReadModel Entry) : ISocketPayload;

public sealed record AlterJournalSocketPayload(AlterJournalReadModel Entry) : ISocketPayload;

public sealed record EntryDeletedSocketPayload(string EntryId) : ISocketPayload;

public sealed record SettingsFieldsUpdatedPayload(IReadOnlyList<SettingsFieldReadModel> Fields) : ISocketPayload;

public sealed record SettingsUsernameUpdatedPayload(string Username) : ISocketPayload;

public sealed record SettingsSelfUpdatedPayload(SocketSelfReadModel Data) : ISocketPayload;

public sealed record DiscordAccountLinkedPayload(string DiscordId) : ISocketPayload;

public sealed record GoogleAccountLinkedPayload(string Email) : ISocketPayload;

public sealed record AppleAccountLinkedPayload(string AppleId) : ISocketPayload;

public sealed record FriendRequestSocketPayload(FriendshipRequestModel Request, FriendProfileReadModel System) : ISocketPayload;

public sealed record FriendIdSocketPayload(string FriendId) : ISocketPayload;

public sealed record SystemIdSocketPayload(string SystemId) : ISocketPayload;

public sealed record ImportCompletedSocketPayload(int AlterCount) : ISocketPayload;
