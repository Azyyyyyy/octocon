namespace Interfold.Domain.Journals;

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record AlterJournalReadModel(
    string Id,
    string UserId,
    int AlterId,
    string Title,
    string? Content,
    string? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt
);