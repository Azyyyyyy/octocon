namespace Interfold.Contracts.Models.Commands;

public sealed record UpdateUsernameCommand(string Username);

public sealed record UnlinkDiscordCommand();

public sealed record UnlinkEmailCommand();

public sealed record UnlinkAppleCommand();

public sealed record DeleteAccountCommand();

public sealed record WipeAltersCommand();

public sealed record WipeTagsCommand();
