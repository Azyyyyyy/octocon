using Cassandra;
using Interfold.Infrastructure.Configuration;
using Interfold.Domain.Settings;
using Interfold.Infrastructure.Persistence.Transient;
using System.Collections.Concurrent;

namespace Interfold.Infrastructure.Persistence.Scylla;

public sealed class ScyllaSettingsFieldRepository : ISettingsFieldRepository
{
    private const short TypeText = 0;
    private const short TypeNumber = 1;
    private const short TypeBoolean = 2;

    private const short SecurityPublic = 0;
    private const short SecurityFriendsOnly = 1;
    private const short SecurityTrustedOnly = 2;
    private const short SecurityPrivate = 3;

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private static readonly ConcurrentDictionary<string, byte> UdtMappings = new(StringComparer.OrdinalIgnoreCase);

    public ScyllaSettingsFieldRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            EnsureFieldUdtMapping(session, keyspace);

            var fields = await LoadFieldsAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return Array.Empty<SettingsFieldReadModel>();
            }

            var result = fields
                .Select((field, index) => new SettingsFieldReadModel(
                    field.Id.ToString("N"),
                    field.Name,
                    ToDomainType(field.Type),
                    ToDomainSecurityLevel(field.SecurityLevel),
                    field.Locked,
                    index))
                .ToArray();

            return (IReadOnlyList<SettingsFieldReadModel>)result;
        }, _options, cancellationToken);
    }

    public async Task<string?> CreateAsync(
        string systemId,
        string name,
        string type,
        string securityLevel,
        bool locked,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            EnsureFieldUdtMapping(session, keyspace);

            var fields = await LoadFieldsAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return null;
            }

            var fieldId = Guid.NewGuid();
            fields.Add(CreateFieldUdt(session, keyspace, fieldId, name, type, securityLevel, locked));

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.users SET fields = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                fields,
                normalizedSystemId));

            return fieldId.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        string systemId,
        string fieldId,
        string? name,
        string? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(fieldId, out var fieldGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            EnsureFieldUdtMapping(session, keyspace);

            var fields = await LoadFieldsAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return false;
            }

            var updated = false;
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Id != fieldGuid)
                {
                    continue;
                }

                if (name is not null)
                {
                    fields[i].Name = name;
                }

                if (securityLevel is not null)
                {
                    fields[i].SecurityLevel = ParseSecurityLevel(securityLevel);
                }

                if (locked is not null)
                {
                    fields[i].Locked = locked.Value;
                }

                updated = true;
                break;
            }

            if (!updated)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.users SET fields = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                fields,
                normalizedSystemId));

            return true;
        }, _options, cancellationToken);
    }

    private static async Task<BatchStatement> RemoveFieldValuesFromAltersAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        Guid fieldId)
    {
        ScyllaAlterRepository.EnsureAlterFieldUdtMapping(session, keyspace);

        var rows = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, fields FROM {keyspace}.alters WHERE user_id = ?",
            normalizedSystemId));

        var batch = new BatchStatement();

        foreach (var row in rows)
        {
            var alterId = row.GetValue<short>("id");
            var fields = row.GetValue<IEnumerable<ScyllaAlterRepository.AlterFieldUdt>?>("fields")?.ToList();
            if (fields is null || fields.Count == 0)
            {
                continue;
            }

            var removedAny = fields.RemoveAll(x => x.Id == fieldId) > 0;
            if (!removedAny)
            {
                continue;
            }

            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.alters SET fields = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                fields,
                normalizedSystemId,
                alterId));
        }

        return batch;
    }

    public async Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(fieldId, out var fieldGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            EnsureFieldUdtMapping(session, keyspace);

            var fields = await LoadFieldsAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return false;
            }

            var removed = fields.RemoveAll(f => f.Id == fieldGuid) > 0;
            if (!removed)
            {
                return false;
            }

            // Remove the field values from all alters before deleting the field itself
            // We want to ensure that the field values are removed to ensure no leakage of deleted field data
            var batch = await RemoveFieldValuesFromAltersAsync(session, keyspace, normalizedSystemId, fieldGuid);
            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET fields = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                fields,
                normalizedSystemId));

            await session.ExecuteAsync(batch);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RelocateAsync(string systemId, string fieldId, int index, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(fieldId, out var fieldGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            EnsureFieldUdtMapping(session, keyspace);

            var fields = await LoadFieldsAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return false;
            }

            var currentIndex = fields.FindIndex(f => f.Id == fieldGuid);
            if (currentIndex < 0)
            {
                return false;
            }

            var field = fields[currentIndex];
            fields.RemoveAt(currentIndex);

            var boundedIndex = Math.Max(0, Math.Min(index, fields.Count));
            fields.Insert(boundedIndex, field);

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.users SET fields = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                fields,
                normalizedSystemId));

            return true;
        }, _options, cancellationToken);
    }

    private static async Task<List<UserFieldUdt>?> LoadFieldsAsync(ISession session, string keyspace, string normalizedSystemId)
    {
        var query = new SimpleStatement(
            $"SELECT fields FROM {keyspace}.users WHERE id = ? LIMIT 1",
            normalizedSystemId);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return row.GetValue<IEnumerable<UserFieldUdt>?>("fields")?.ToList() ?? [];
    }

    private static UserFieldUdt CreateFieldUdt(
        ISession session,
        string keyspace,
        Guid id,
        string name,
        string type,
        string securityLevel,
        bool locked)
    {
        EnsureFieldUdtMapping(session, keyspace);
        return new UserFieldUdt
        {
            Id = id,
            Name = name,
            Type = ParseType(type),
            Locked = locked,
            SecurityLevel = ParseSecurityLevel(securityLevel)
        };
    }

    private static void EnsureFieldUdtMapping(ISession session, string keyspace)
    {
        if (UdtMappings.ContainsKey(keyspace))
        {
            return;
        }

        session.UserDefinedTypes.Define(
            UdtMap
                .For<UserFieldUdt>("field", keyspace)
                .Map(f => f.Id, "id")
                .Map(f => f.Name, "name")
                .Map(f => f.Type, "type")
                .Map(f => f.Locked, "locked")
                .Map(f => f.SecurityLevel, "security_level"));

        UdtMappings.TryAdd(keyspace, 0);
    }

    private static short ParseType(string type)
    {
        return type switch
        {
            "text" => TypeText,
            "number" => TypeNumber,
            "boolean" => TypeBoolean,
            _ => TypeText
        };
    }

    private static short ParseSecurityLevel(string securityLevel)
    {
        return securityLevel switch
        {
            "public" => SecurityPublic,
            "friends_only" => SecurityFriendsOnly,
            "trusted_only" => SecurityTrustedOnly,
            _ => SecurityPrivate
        };
    }

    private static string ToDomainType(short type)
    {
        return type switch
        {
            TypeNumber => "number",
            TypeBoolean => "boolean",
            _ => "text"
        };
    }

    private static string ToDomainSecurityLevel(short securityLevel)
    {
        return securityLevel switch
        {
            SecurityPublic => "public",
            SecurityFriendsOnly => "friends_only",
            SecurityTrustedOnly => "trusted_only",
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

    private sealed class UserFieldUdt
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public short Type { get; set; }
        public bool Locked { get; set; }
        public short SecurityLevel { get; set; }
    }
}
