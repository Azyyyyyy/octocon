namespace Interfold.Contracts.Models.Read;

public sealed record SettingsFieldReadModel(
    string Id,
    string Name,
    string Type,
    string SecurityLevel,
    bool Locked,
    int Index);