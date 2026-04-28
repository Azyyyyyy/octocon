namespace Interfold.Contracts.Models.Commands;

public sealed record AddPushTokenCommand(string Token);

public sealed record RemovePushTokenCommand(string Token);

public sealed record UpdateDescriptionCommand(string Description);
