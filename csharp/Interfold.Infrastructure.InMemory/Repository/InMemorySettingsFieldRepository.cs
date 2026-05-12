using System.Collections.Concurrent;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemorySettingsFieldRepository : ISettingsFieldRepository
{
    private readonly IRegionContext _regionContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, List<SettingsFieldReadModel>> _bySystem = new(StringComparer.Ordinal);

    public InMemorySettingsFieldRepository(IRegionContext regionContext, IServiceProvider serviceProvider)
    {
        _regionContext = regionContext;
        _serviceProvider = serviceProvider;
    }

    public Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(Array.Empty<SettingsFieldReadModel>());
        }

        lock (store)
        {
            var result = store
                .OrderBy(x => x.Index)
                .Select(x => x)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(result);
        }
    }

    public Task<string?> CreateAsync(
        string systemId,
        string name,
        string type,
        string securityLevel,
        bool locked,
        CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new List<SettingsFieldReadModel>());
        var fieldId = Guid.NewGuid().ToString("N");

        lock (store)
        {
            var normalizedType = NormalizeType(type);
            var normalizedSecurity = NormalizeSecurityLevel(securityLevel);
            store.Add(new SettingsFieldReadModel(fieldId, name, normalizedType, normalizedSecurity, locked, store.Count));
        }

        return Task.FromResult<string?>(fieldId);
    }

    public Task<bool> UpdateAsync(
        string systemId,
        string fieldId,
        string? name,
        string? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var index = store.FindIndex(x => string.Equals(x.Id, fieldId, StringComparison.Ordinal));
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var existing = store[index];
            store[index] = existing with
            {
                Name = name ?? existing.Name,
                SecurityLevel = securityLevel is not null ? NormalizeSecurityLevel(securityLevel) : existing.SecurityLevel,
                Locked = locked ?? existing.Locked
            };
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default)
    {
        if (!TryParseUuid(fieldId, out var fieldGuid))
        {
            return Task.FromResult(false);
        }

        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var index = store.FindIndex(x => string.Equals(x.Id, fieldId, StringComparison.Ordinal));
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            store.RemoveAt(index);
            Reindex(store);
        }

        // Cascade delete field values from alters in the in-memory alter repository (if present)
        try
        {
            var alterRepo = _serviceProvider?.GetService<IAlterRepository>();
            if (alterRepo is InMemoryAlterRepository regional)
            {
                regional.RemoveFieldValuesForSystem(systemId, fieldGuid);
            }
        }
        catch
        {
            // Best-effort cascade; don't fail deletion if alters update isn't available
        }

        return Task.FromResult(true);
    }

    public Task<bool> RelocateAsync(string systemId, string fieldId, int index, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var oldIndex = store.FindIndex(x => string.Equals(x.Id, fieldId, StringComparison.Ordinal));
            if (oldIndex < 0)
            {
                return Task.FromResult(false);
            }

            var field = store[oldIndex];
            store.RemoveAt(oldIndex);

            var boundedIndex = Math.Max(0, Math.Min(index, store.Count));
            store.Insert(boundedIndex, field);
            Reindex(store);
            return Task.FromResult(true);
        }
    }

    private static void Reindex(List<SettingsFieldReadModel> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            fields[i] = fields[i] with { Index = i };
        }
    }

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }

    private static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "text";

        return type.Trim().ToLowerInvariant() switch
        {
            "number" => "number",
            "boolean" => "boolean",
            "date" => "date",
            "colour" => "colour",
            "plaintext" => "plaintext",
            "month" => "month",
            "year" => "year",
            "month_year" => "month_year",
            "timestamp" => "timestamp",
            "month_day" => "month_day",
            _ => "text"
        };
    }

    private static string NormalizeSecurityLevel(string? securityLevel)
    {
        if (string.IsNullOrWhiteSpace(securityLevel))
            return "private";

        return securityLevel.Trim().ToLowerInvariant() switch
        {
            "public" => "public",
            "friends_only" => "friends_only",
            "trusted_only" => "trusted_only",
            _ => "private"
        };
    }

    internal static bool TryParseUuid(string value, out Guid guid)
    {
        if (Guid.TryParseExact(value, "N", out guid))
        {
            return true;
        }

        return Guid.TryParse(value, out guid);
    }
}
