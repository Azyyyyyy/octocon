namespace Interfold.Contracts.Models.Read;

public sealed record SettingsFieldReadModel(
    string Id,
    string Name,
    string Type,
    string SecurityLevel,
    bool Locked,
    int Index);
    
public sealed record SettingsUsernameRequest(
    string Username,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsDescriptionRequest(
    string Description,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsPushTokenRequest(
    string? Token = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsEncryptionRequest(
    string RecoveryCode,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsImportRequest(
    string Token,
    string? RecoveryCode = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record AvatarUrlUploadRequest(
    string Url,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsCreateFieldRequest(
    string Name,
    string? Type,
    string? SecurityLevel,
    bool? Locked,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsUpdateFieldRequest(
    string? Name,
    string? SecurityLevel,
    bool? Locked,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SettingsRelocateFieldRequest(
    int Index,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record AvatarUploadPayload(Stream? Stream, string? IdempotencyKey, bool EmptyFilePart = false) : BaseRequest(IdempotencyKey);
