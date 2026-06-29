using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Web;
using Interfold.Api.Services;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.IntegrationTests.SimplyPluralImport;

/// <summary>
/// Regression coverage for the Simply Plural import "today-date" bug fix. Every test wires up the
/// real <see cref="SimplyPluralImportService"/> against the InMemory persistence stack and feeds
/// it canned SP responses through <see cref="TestServices.StubSpHandler"/>, so we keep coverage
/// after Apparyllis sunsets the live API.
///
/// Data hygiene: every test mints fresh identifiers via <see cref="Uid"/> / <see cref="MemberUuid"/>
/// and uses obviously-synthetic timestamps (never the real values from the production dry-run
/// captures). No real account data lives in this file.
/// </summary>
public sealed class SpImportTests : BaseEndpointTest
{
    private static string Uid() => $"sys-{Guid.NewGuid():N}"[..16];
    private static string MemberUuid() => Guid.NewGuid().ToString("N");

    // Synthetic timestamps (ms since Unix epoch). The whole point of this test file is to
    // verify the import respects these dates *exactly* and never silently substitutes today.
    private const long Date_2020_01_15 = 1579082400000;     // 2020-01-15T10:00:00Z
    private const long Date_2021_06_10 = 1623333600000;     // 2021-06-10T14:00:00Z
    private const long Date_2021_06_10_End = 1623337200000; // 2021-06-10T15:00:00Z
    private const long Date_2022_03_05 = 1646474400000;     // 2022-03-05T10:00:00Z

    [Test]
    public async Task LiveFronter_WithRealStartTime_PreservesPastDate()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var frontId = MemberUuid();

        // Sticky live front with a real past startTime — must be preserved verbatim.
        var liveFronters = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = frontId,
                content = new { member = alpha, startTime = Date_2020_01_15, live = true },
            },
        });

        var stub = BuildSp(sysId, alpha, currentFrontersJson: liveFronters);

        var (sp, frontingRepo, _) = await RunImportAsync(stub, systemId);
        await Assert.That(sp).IsNotNull();

        var active = await frontingRepo.ListActiveAsync(systemId);
        await Assert.That(active.Count).IsEqualTo(1);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(Date_2020_01_15);
        await Assert.That(active[0].Front.TimeStart).IsEqualTo(expected);

        // Defensive: an accidental DateTimeOffset.UtcNow substitution would land within
        // a few seconds of "now"; the synthetic date is years in the past.
        await Assert.That(active[0].Front.TimeStart)
            .IsLessThan(DateTimeOffset.UtcNow.AddDays(-30));
    }

    [Test]
    public async Task LiveFronter_StartTimeZero_FallsBackToLastOperationTime()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var frontId = MemberUuid();

        // SP omits startTime on some sticky/primary live fronts but still emits
        // lastOperationTime. The fix should use that as the fallback rather than today.
        var liveFronters = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = frontId,
                content = new
                {
                    member = alpha,
                    startTime = 0L,
                    lastOperationTime = Date_2021_06_10,
                    live = true,
                },
            },
        });

        var stub = BuildSp(sysId, alpha, currentFrontersJson: liveFronters);

        var (_, frontingRepo, _) = await RunImportAsync(stub, systemId);

        var active = await frontingRepo.ListActiveAsync(systemId);
        await Assert.That(active.Count).IsEqualTo(1);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10);
        await Assert.That(active[0].Front.TimeStart).IsEqualTo(expected);
        await Assert.That(active[0].Front.TimeStart)
            .IsLessThan(DateTimeOffset.UtcNow.AddDays(-30));
    }

    [Test]
    public async Task LiveFronter_BothZero_SkippedWithWarning()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var frontId = MemberUuid();

        // Both startTime and lastOperationTime missing — must be skipped, never stamped today.
        var liveFronters = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = frontId,
                content = new
                {
                    member = alpha,
                    startTime = 0L,
                    lastOperationTime = 0L,
                    live = true,
                },
            },
        });

        var stub = BuildSp(sysId, alpha, currentFrontersJson: liveFronters);

        var (_, frontingRepo, logger) = await RunImportAsync(stub, systemId);

        var active = await frontingRepo.ListActiveAsync(systemId);
        await Assert.That(active.Count).IsEqualTo(0);

        var warnings = logger.Records
            .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("Skipping live SP fronter"))
            .ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(warnings.Length).IsEqualTo(1);
            // Confirm the warning mentions the synthetic SP member uuid so the operator
            // can trace which row was skipped without enabling debug logging.
            await Assert.That(warnings[0].Message).Contains(alpha);
        }
    }

    [Test]
    public async Task HistoricalFront_StartAndEnd_RetainOriginalTimes()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var historicalFrontId = MemberUuid();

        // Closed historical front in a single past chunk. ImportFrontsAsync sends one request
        // per chunk window with startTime/endTime query params; OnGetPrefix lets the same body
        // serve every chunk request. Each chunk returning the same payload is fine because
        // ImportFrontsAsync dedupes by SpEntity.Id.
        var historyJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = historicalFrontId,
                content = new
                {
                    member = alpha,
                    startTime = Date_2021_06_10,
                    endTime = Date_2021_06_10_End,
                },
            },
        });

        var stub = BuildSp(sysId, alpha, frontHistoryJson: historyJson, currentFrontersJson: "[]");

        var (_, frontingRepo, _) = await RunImportAsync(stub, systemId);

        var history = await frontingRepo.ListHistoryBetweenAsync(
            systemId,
            DateTimeOffset.FromUnixTimeMilliseconds(0),
            DateTimeOffset.UtcNow.AddYears(1));

        await Assert.That(history.Count).IsEqualTo(1);
        var entry = history[0];
        using (Assert.Multiple())
        {
            await Assert.That(entry.TimeStart).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10));
            await Assert.That(entry.TimeEnd).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10_End));
        }
    }

    [Test]
    public async Task ChunkLoop_IssuesExactlyNumberOfChunksRequests_NoOffByOne()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        var stub = BuildSp(sysId, alpha,
            frontHistoryJson: "[]",
            currentFrontersJson: "[]");

        await RunImportAsync(stub, systemId);

        // ImportFrontsAsync chunks the SP epoch (2015-01-01) → now in 6-month windows.
        // Pre-fix the loop ran `i <= numberOfChunks`, issuing one extra request that
        // always returned `[]`. Compute the expected count with the same formula the
        // production code uses so the assertion stays correct as time marches on.
        const long startEpoch = 1_420_070_400_000;
        const int monthInterval = 6;
        var chunkSizeMs = (long)monthInterval * 30 * 24 * 60 * 60 * 1000;
        var endTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expectedChunks = (int)Math.Ceiling((double)(endTimeMs - startEpoch) / chunkSizeMs);

        var historyRequests = stub.RequestsTo($"/v1/frontHistory/{sysId}");
        await Assert.That(historyRequests.Count).IsEqualTo(expectedChunks);

        // Sanity-check: no captured chunk should have startTime > endTime (the off-by-one
        // bug produced exactly that on its final iteration).
        foreach (var url in historyRequests)
        {
            var queryIndex = url.IndexOf('?');
            await Assert.That(queryIndex).IsGreaterThan(-1);
            var query = HttpUtility.ParseQueryString(url[(queryIndex + 1)..]);
            var startMs = long.Parse(query["startTime"]!, CultureInfo.InvariantCulture);
            var endMs = long.Parse(query["endTime"]!, CultureInfo.InvariantCulture);
            await Assert.That(startMs).IsLessThanOrEqualTo(endMs);
        }
    }

    /// <summary>
    /// Builds the canonical "minimal valid SP backend" stub. The single SP member <paramref name="alpha"/>
    /// is wired up so the import creates exactly one alter, which is the only association
    /// front-related assertions care about. Override the front-related blobs per test.
    /// </summary>
    private static TestServices.StubSpHandler BuildSp(
        string sysId,
        string alpha,
        string? frontHistoryJson = null,
        string? currentFrontersJson = null)
    {
        var meJson = JsonSerializer.Serialize(new
        {
            exists = true,
            id = sysId,
            content = new { desc = "synthetic test system" },
        });

        var membersJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = alpha,
                content = new
                {
                    name = "alpha",
                    date = Date_2022_03_05,
                    lastOperationTime = Date_2022_03_05,
                },
            },
        });

        return new TestServices.StubSpHandler()
            .OnGet("/v1/me", meJson)
            .OnGet($"/v1/customFields/{sysId}", "[]")
            .OnGet($"/v1/members/{sysId}", membersJson)
            .OnGet($"/v1/customFronts/{sysId}", "[]")
            .OnGet($"/v1/groups/{sysId}", "[]")
            .OnGetPrefix($"/v1/frontHistory/{sysId}", frontHistoryJson ?? "[]")
            .OnGet("/v1/fronters/", currentFrontersJson ?? "[]")
            .OnGet($"/v1/polls/{sysId}", "[]");
    }

    private static async Task<(SpImportResult Result, IFrontingRepository FrontingRepo, CapturingLogger Logger)> RunImportAsync(
        TestServices.StubSpHandler stub,
        string systemId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationManager());

        var logger = new CapturingLogger();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        services.AddLogging();

        InMemoryServiceCollectionExtensions.Register();
        services.AddInterfoldPersistence(PersistenceMode.InMemory, cfg =>
        {
            cfg.ScyllaKeyspace = "nam";
        });
        services.AddInterfoldDomainHandlers();

        services.AddOptions<AuthenticationConfiguration>();
        services.AddSingleton<IAvatarStorage, NullAvatarStorage>();

        services.AddHttpClient("SimplyPlural")
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var importService = scope.ServiceProvider.GetRequiredService<ISimplyPluralImportService>();
        importService.WaitForAvatars = true;

        // encryptionKey is null → ImportAsync skips ValidateEncryptionKeyAsync; notes are skipped too.
        // spToken is a synthetic placeholder — StubSpHandler doesn't check it.
        var result = await importService.ImportAsync(systemId, "synthetic-token", encryptionKey: null);

        var frontingRepo = scope.ServiceProvider.GetRequiredService<IFrontingRepository>();
        return (result, frontingRepo, logger);
    }

    private sealed class NullAvatarStorage : IAvatarStorage
    {
        public Task<string> SaveSystemAvatarAsync(string systemId, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<string> SaveAlterAvatarAsync(string systemId, int alterId, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<bool> DeleteByUrlAsync(string? avatarUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// Minimal in-tree FakeLogger replacement. Avoids pulling in
    /// <c>Microsoft.Extensions.Diagnostics.Testing</c> just for one assertion. Captures the
    /// formatted message and level so tests can grep for the specific warning emitted by
    /// the skip+warn branch in ImportFrontsAsync.
    /// </summary>
    private sealed record CapturedLog(LogLevel Level, string Message, string? Category);

    private sealed class CapturingLogger : ILogger
    {
        public List<CapturedLog> Records { get; } = new();
        public string? Category { get; set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Records)
            {
                Records.Add(new CapturedLog(logLevel, formatter(state, exception), Category));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly CapturingLogger _logger;
        public CapturingLoggerFactory(CapturingLogger logger) => _logger = logger;

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName)
        {
            _logger.Category ??= categoryName;
            return _logger;
        }

        public void Dispose() { }
    }
}
