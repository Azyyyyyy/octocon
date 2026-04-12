using System.Net;
using System.Text.Json;

namespace Interfold.IntegrationTests;

/// <summary>
/// Integration tests for node-role detection and the <c>GET /health/node-role</c> endpoint.
/// Gated on <c>OCTOCON_RUN_API_INTEGRATION=true</c>.
/// </summary>
public sealed class NodeRoleIntegrationTests
{
    [Test]
    public async Task NodeRole_DefaultsToAuxiliary_WhenNoEnvVarSet()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        Ensure(role == "auxiliary",
            $"Expected role=auxiliary when no env var set. Got: {body}");
        Ensure(ownsSingletons == false,
            $"Expected owns_singletons=false for auxiliary. Got: {body}");
    }

    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenOctoconNodeGroupIsPrimary()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        Ensure(role == "primary",
            $"Expected role=primary when OCTOCON_NODE_GROUP=primary. Got: {body}");
        Ensure(ownsSingletons == true,
            $"Expected owns_singletons=true for primary. Got: {body}");
    }

    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenFlyProcessGroupIsPrimary()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        // FLY_PROCESS_GROUP takes precedence; OCTOCON_NODE_GROUP is not set.
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("FLY_PROCESS_GROUP", "primary");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");

        Ensure(role == "primary",
            $"Expected role=primary when FLY_PROCESS_GROUP=primary. Got: {body}");
    }

    [Test]
    public async Task NodeRole_FlyProcessGroup_TakesPrecedenceOver_OctoconNodeGroup()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        // FLY_PROCESS_GROUP=sidecar should win over OCTOCON_NODE_GROUP=primary.
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary")
            .WithConfiguration("FLY_PROCESS_GROUP", "sidecar");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        Ensure(role == "sidecar",
            $"Expected role=sidecar (FLY_PROCESS_GROUP wins). Got: {body}");
        Ensure(ownsSingletons == false,
            $"Expected owns_singletons=false for sidecar. Got: {body}");
    }

    [Test]
    public async Task NodeRole_Endpoint_IsAnonymous()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        using var client = factory.CreateClient();

        // Un-authenticated request — must not return 401.
        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected /health/node-role to be anonymous (200), got {(int)response.StatusCode}.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ReadStringField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                return prop.Value.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static bool ReadBoolField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                _ => throw new InvalidOperationException($"Expected boolean for '{fieldName}'. Got: {json}")
            };
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
