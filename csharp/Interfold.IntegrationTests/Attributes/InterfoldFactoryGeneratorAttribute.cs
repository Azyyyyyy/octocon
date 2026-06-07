using Interfold.IntegrationTests.Models;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Attributes;

public class InterfoldFactoryGeneratorAttribute : DataSourceGeneratorAttribute<InterfoldWebApplicationFactory>
{
    protected override IEnumerable<Func<InterfoldWebApplicationFactory>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        yield return () => new InterfoldWebApplicationFactory("inmemory");
        
        if (IntegrationTestEnvironment.HasPostgresConnection)
        {
            yield return () => new InterfoldWebApplicationFactory("scylla-postgres")
                .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", IntegrationTestEnvironment.PostgresConnection)
                .WithConfiguration("OCTOCON_SCYLLA_CONTACT_POINTS", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_CONTACT_POINTS", "127.0.0.1"))
                .WithConfiguration("OCTOCON_SCYLLA_USERNAME", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_USERNAME", "cassandra"))
                .WithConfiguration("OCTOCON_SCYLLA_PASSWORD", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_PASSWORD", "cassandra"))
                .WithConfiguration("OCTOCON_SCYLLA_KEYSPACE", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_KEYSPACE", "nam"))
                .WithConfiguration("OCTOCON_SCYLLA_DATACENTER", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_DATACENTER", "datacenter1"));
        }
    }
}