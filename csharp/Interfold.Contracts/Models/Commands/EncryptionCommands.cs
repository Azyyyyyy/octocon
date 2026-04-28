namespace Interfold.Contracts.Models.Commands;

public sealed record SetupEncryptionCommand(string RecoveryCode);

public sealed record RecoverEncryptionCommand(string RecoveryCode);

public sealed record ResetEncryptionCommand();
