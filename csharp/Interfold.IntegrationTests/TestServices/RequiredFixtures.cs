namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Process-static record of which <see cref="IWebFactoryFixture"/> implementations the current
/// test session may request. Populated once by a TUnit
/// <c>[Before(HookType.TestDiscovery)]</c> hook (see
/// <c>BaseEndpointTest.RegisterRequiredFixtures</c>) — anchored to that single lifecycle
/// point rather than scanned lazily on every property read.
/// </summary>
/// <remarks>
/// <para>
/// We scan from <c>[Before(TestDiscovery)]</c> rather than <c>[Before(TestSession)]</c>
/// because TUnit's <c>PerTestSession</c> <c>[ClassDataSource]</c> fixtures are constructed
/// (and their <c>IAsyncInitializer.InitializeAsync</c> called) during session bootstrapping,
/// which timestamped tracing showed lands ~500ms <em>before</em> session-level hooks fire.
/// <c>BeforeTestDiscoveryContext</c> doesn't yet expose a test class list — discovery is what
/// the hook precedes — so the scan walks loaded test-assembly types instead. This is the
/// exact pattern the TUnit team recommends in
/// <see href="https://github.com/thomhurst/TUnit/discussions/1496">Discussion #1496</see>
/// for cross-assembly setup that has to run before the test graph touches any class.
/// </para>
/// <para>
/// <c>ClassDataSourceAttribute&lt;T&gt;</c> doesn't expose its type argument through a
/// strongly-typed runtime API, so the hook reaches for <see cref="System.Reflection"/> to
/// pull the generic argument out of the attribute on each test class. The reflection is
/// bounded to a single hook invocation that runs once per process.
/// </para>
/// </remarks>
internal static class RequiredFixtures
{
    private static readonly HashSet<Type> Discovered = new();

    public static bool NeedScylla => Discovered.Contains(typeof(ScyllaWebFactoryFixture));

    public static bool NeedCassandra => Discovered.Contains(typeof(CassandraWebFactoryFixture));

    /// <summary>
    /// Idempotent: re-invocation in the same process is a no-op once the set is populated, so
    /// nested test sessions or accidental hook re-entry don't redo the walk.
    /// </summary>
    public static void Discover()
    {
        if (Discovered.Count > 0)
        {
            return;
        }

        var assembly = typeof(RequiredFixtures).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var fixtureType in ExtractClassDataSourceTypes(type))
            {
                Discovered.Add(fixtureType);
            }
        }
    }

    /// <summary>
    /// Yields the generic arguments of every <c>TUnit.Core.ClassDataSourceAttribute&lt;...&gt;</c>
    /// declared on <paramref name="testClassType"/> (or any base class). Generic arities 1..5
    /// are all covered by the <c>StartsWith</c> match on the open-generic full name.
    /// </summary>
    private static IEnumerable<Type> ExtractClassDataSourceTypes(Type testClassType)
    {
        foreach (var attr in testClassType.GetCustomAttributes(inherit: true))
        {
            var attrType = attr.GetType();
            if (!attrType.IsGenericType)
            {
                continue;
            }

            var openGeneric = attrType.GetGenericTypeDefinition();
            if (openGeneric.FullName?.StartsWith(
                    "TUnit.Core.ClassDataSourceAttribute`",
                    StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (var t in attrType.GetGenericArguments())
            {
                yield return t;
            }
        }
    }
}
