using Cassandra;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Topology;

/// <summary>
/// Tests that a multi-node ScyllaDB cluster (7 regional DCs) correctly spins up
/// and can serve cross-DC queries. Uses a dedicated <see cref="MultiNodeScyllaFixture"/>
/// managed by TUnit.Aspire that shares Postgres with <see cref="SharedDbFixture"/>.
/// </summary>
[ClassDataSource<MultiNodeScyllaFixture>(Shared = SharedType.PerTestSession)]
public sealed class MultiNodeScyllaTests(MultiNodeScyllaFixture fixture)
{
    private static readonly string[] ExpectedRegions = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];

    [Test]
    public async Task MultiNodeScylla_AllNodesReachUpNormalState()
    {
        var cluster = Cluster.Builder()
            .AddContactPoint("127.0.0.1")
            .WithPort(fixture.ScyllaPort)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("nam"))
            .WithCredentials("test_app_user", "test_secure_pw_123!Safe")
            .WithQueryTimeout(30000)
            .Build();

        var session = await cluster.ConnectAsync();

        try
        {
            var visibleDcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var localRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.local"));
            foreach (var row in localRows)
            {
                var dc = row.GetValue<string>("data_center");
                if (!string.IsNullOrWhiteSpace(dc))
                    visibleDcs.Add(dc.ToLowerInvariant());
            }

            var peerRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.peers"));
            foreach (var row in peerRows)
            {
                var dc = row.GetValue<string>("data_center");
                if (!string.IsNullOrWhiteSpace(dc))
                    visibleDcs.Add(dc.ToLowerInvariant());
            }

            foreach (var region in ExpectedRegions)
            {
                await Assert.That(visibleDcs.Contains(region)).IsTrue()
                    .Because($"Expected DC '{region}' to be visible in cluster");
            }
        }
        finally
        {
            session.Dispose();
            await cluster.ShutdownAsync();
        }
    }

    [Test]
    [DependsOn(nameof(MultiNodeScylla_AllNodesReachUpNormalState))]
    public async Task MultiNodeScylla_CrossDcCqlQuerySucceeds()
    {
        var namCluster = Cluster.Builder()
            .AddContactPoint("127.0.0.1")
            .WithPort(fixture.ScyllaPort)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("nam"))
            .WithCredentials("test_app_user", "test_secure_pw_123!Safe")
            .WithQueryTimeout(30000)
            .Build();

        var namSession = await namCluster.ConnectAsync();

        try
        {
            var testId = $"test-{Guid.NewGuid():N}"[..20];

            await namSession.ExecuteAsync(new SimpleStatement(
                "INSERT INTO global.user_registry (id, region) VALUES (?, ?)", testId, "nam"));

            var result = await namSession.ExecuteAsync(new SimpleStatement(
                "SELECT region FROM global.user_registry WHERE id = ?", testId));

            var row = result.FirstOrDefault();
            await Assert.That(row).IsNotNull();
            await Assert.That(row!.GetValue<string>("region")).IsEqualTo("nam");

            await namSession.ExecuteAsync(new SimpleStatement(
                "DELETE FROM global.user_registry WHERE id = ?", testId));
        }
        finally
        {
            namSession.Dispose();
            await namCluster.ShutdownAsync();
        }
    }
}
