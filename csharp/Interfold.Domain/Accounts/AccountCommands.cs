namespace Interfold.Domain.Accounts;

public sealed record UpdateUsernameCommand(string Username);

public sealed record SetupEncryptionCommand(string RecoveryCode);

public sealed record RecoverEncryptionCommand(string RecoveryCode);
