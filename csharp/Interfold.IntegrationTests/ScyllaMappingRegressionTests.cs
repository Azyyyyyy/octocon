using Cassandra;
using Microsoft.Extensions.Logging.Abstractions;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence.Scylla;

namespace Interfold.IntegrationTests;

public sealed class ScyllaMappingRegressionTests
{
    [Test]
    public void PollUuidParsing_AcceptsCompactAndHyphenated_RejectsInvalid()
    {
        var compact = Guid.NewGuid().ToString("N");
        var hyphenated = Guid.NewGuid().ToString("D");

        var compactOk = ScyllaPollRepository.TryParseUuid(compact, out var compactParsed);
        var hyphenatedOk = ScyllaPollRepository.TryParseUuid(hyphenated, out var hyphenatedParsed);
        var invalidOk = ScyllaPollRepository.TryParseUuid("not-a-guid", out _);

        Ensure(compactOk, "Compact GUID format should parse.");
        Ensure(hyphenatedOk, "Hyphenated GUID format should parse.");
        Ensure(!invalidOk, "Invalid GUID should fail parsing.");
        Ensure(compactParsed.ToString("N") == compact, "Parsed compact GUID should round-trip.");
        Ensure(hyphenatedParsed.ToString("D") == hyphenated, "Parsed hyphenated GUID should round-trip.");
    }

    [Test]
    public void PollTypeMapping_HandlesKnownAndBoundaryValues()
    {
        Ensure(ScyllaPollRepository.ToPollCode("single_choice") == 0, "single_choice should map to 0.");
        Ensure(ScyllaPollRepository.ToPollCode("multiple_choice") == 1, "multiple_choice should map to 1.");
        Ensure(ScyllaPollRepository.ToPollCode("approval") == 2, "approval should map to 2.");
        Ensure(ScyllaPollRepository.ToPollCode("unknown-type") == 0, "Unknown poll type should default to 0.");

        Ensure(ScyllaPollRepository.ToPollType(0) == "vote", "Code 0 should map to vote.");
        Ensure(ScyllaPollRepository.ToPollType(1) == "choice", "Code 1 should map to choice.");
        Ensure(ScyllaPollRepository.ToPollType(2) == "approval", "Code 2 should map to approval.");
        Ensure(ScyllaPollRepository.ToPollType(short.MinValue) == "vote", "Lower boundary should default to vote.");
        Ensure(ScyllaPollRepository.ToPollType(short.MaxValue) == "vote", "Upper boundary should default to vote.");
    }

    [Test]
    public void FriendshipLevelMapping_HandlesKnownAndBoundaryValues()
    {
        Ensure(ScyllaFriendshipRepository.ToDomainLevel(0) == "friend", "Level 0 should map to friend.");
        Ensure(ScyllaFriendshipRepository.ToDomainLevel(1) == "trusted_friend", "Level 1 should map to trusted_friend.");
        Ensure(ScyllaFriendshipRepository.ToDomainLevel(short.MinValue) == "friend", "Lower boundary should default to friend.");
        Ensure(ScyllaFriendshipRepository.ToDomainLevel(short.MaxValue) == "friend", "Upper boundary should default to friend.");
    }

    [Test]
    public void UuidParsingRegression_TagJournalSettings_AcceptsCompactAndRejectsInvalid()
    {
        var tag = Guid.NewGuid().ToString("N");
        var journal = Guid.NewGuid().ToString("N");
        var field = Guid.NewGuid().ToString("N");

        Ensure(ScyllaTagRepository.TryParseUuid(tag, out _), "Tag UUID should parse.");
        Ensure(ScyllaJournalRepository.TryParseUuid(journal, out _), "Journal UUID should parse.");
        Ensure(ScyllaSettingsFieldRepository.TryParseUuid(field, out _), "Settings field UUID should parse.");

        Ensure(!ScyllaTagRepository.TryParseUuid("bad", out _), "Invalid tag UUID should fail.");
        Ensure(!ScyllaJournalRepository.TryParseUuid("bad", out _), "Invalid journal UUID should fail.");
        Ensure(!ScyllaSettingsFieldRepository.TryParseUuid("bad", out _), "Invalid settings field UUID should fail.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed class RegionContextCachingTests
{
    // A stub that throws if the session is ever accessed, proving the cache is always used.
    private sealed class ThrowingSessionProvider : IScyllaSessionProvider
    {
        public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DB must not be reached when cache is warm.");
    }

    private static ScyllaUserRegistryRegionContext BuildContext(string defaultRegion = "nam") =>
        new(new ThrowingSessionProvider(), new PersistenceConfiguration { DefaultRegion = defaultRegion },
            NullLogger<ScyllaUserRegistryRegionContext>.Instance);

    [Test]
    public void ResolveUserRegion_FallsBackToDefault_WhenSessionThrowsAndCacheEmpty()
    {
        // ThrowingSessionProvider will throw; the catch block should return CurrentRegion.
        var ctx = BuildContext("eur");
        var result = ctx.ResolveUserRegion("user-123");
        Ensure(result == "eur", $"Expected fallback 'eur', got '{result}'.");
    }

    [Test]
    public void ResolveUserRegion_UsesCachedRegion_AfterRegisterRegion()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("user-abc", "eur");

        // ThrowingSessionProvider must NOT be called; cache is warm.
        var result = ctx.ResolveUserRegion("user-abc");
        Ensure(result == "eur", $"Expected cached 'eur', got '{result}'.");
    }

    [Test]
    public void ResolveUserRegion_StripsLegacyPrefix_BeforeCacheLookup()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("eas", "user-xyz");  // plain key stored as "user-xyz"

        // The prefixed form should resolve via the same stripped key.
        ctx.RegisterRegion("eas:user-xyz", "sam");
        var result = ctx.ResolveUserRegion("eas:user-xyz");
        Ensure(result == "sam", $"Expected 'sam' after prefixed RegisterRegion, got '{result}'.");

        // Plain key lookup after prefix strip should also be cache-warm.
        var result2 = ctx.ResolveUserRegion("eas:user-xyz");
        Ensure(result2 == "sam", $"Expected 'sam' on repeat call, got '{result2}'.");
    }

    [Test]
    public void ResolveConsistency_ReturnsLocal_ForSameRegion()
    {
        var ctx = BuildContext("nam");
        Ensure(ctx.ResolveConsistency("nam") == "local", "Same region should be 'local'.");
        Ensure(ctx.ResolveConsistency("NAM") == "local", "Same region (case-insensitive) should be 'local'.");
    }

    [Test]
    public void ResolveConsistency_ReturnsGlobal_ForDifferentRegion()
    {
        var ctx = BuildContext("nam");
        Ensure(ctx.ResolveConsistency("eur") == "global", "Different region should be 'global'.");
    }

    [Test]
    public void RegisterRegion_EmptyValues_DoesNotCorruptCache()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("", "eur");        // empty key — should be no-op
        ctx.RegisterRegion("user-1", "");     // empty region — should be no-op

        // Fallback should still apply because nothing was cached.
        var result = ctx.ResolveUserRegion("user-1");
        Ensure(result == "nam", $"Expected fallback 'nam', got '{result}'.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
