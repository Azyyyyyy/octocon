using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models.Commands;

public sealed record UploadAvatarCommand(string AvatarUrl, AvatarSource Source);

public sealed record DeleteAvatarCommand();
