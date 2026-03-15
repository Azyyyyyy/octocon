namespace Octocon.Domain.Settings;

public sealed record SetupEncryptionCommand(string RecoveryCode);

public sealed record RecoverEncryptionCommand(string RecoveryCode);

public sealed record ResetEncryptionCommand();
