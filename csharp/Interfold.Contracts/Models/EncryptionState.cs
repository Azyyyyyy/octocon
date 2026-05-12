namespace Interfold.Contracts.Models;

public sealed record EncryptionState(bool Initialized, string? KeyChecksum, string? Salt);