using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the opt-in <c>deployment.includeWeb</c> path with TLS termination
/// turned off (<c>deployment.webHttps=false</c>). This is the HTTP-only debug variant — the
/// bootstrapper must:
/// <list type="bullet">
///   <item>Emit the <c>octocon-web</c> service in the generated compose so the wasm UI ships
///         alongside the API.</item>
///   <item>NOT bind-mount the leaf cert or nginx envsubst template — those are only useful for
///         the TLS-terminating variant covered by <see cref="WebHttpsTests"/>.</item>
///   <item>Map the operator's <c>ports.webHttp</c> / <c>ports.webHttps</c> entries onto the
///         upstream image's <c>:8080</c> listener (it ONLY listens there per its
///         <c>Dockerfile.wasm</c>) and emit an HTTP healthcheck against that port.</item>
/// </list>
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class IncludeWebHttpOnlyTests(UbuntuDinDFixture dinD)
{
    private static string IncludeWebHttpOnlyConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.web-http-only.json");

    [After(Test)]
    public async Task DumpOnFailure(TestContext ctx)
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }
        await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
    }

    [Test]
    public async Task PublishWithIncludeWebHttpOnlyEmitsServiceWithoutTlsWiring()
    {
        // Pure publish-time contract: includeWeb=true && webHttps=false should land the
        // octocon-web service in the compose YAML, but WITHOUT the TLS-only bind mounts and env
        // vars. This is the "ship the wasm container for debugging behind an external proxy"
        // shape — the operator wants the container, doesn't want the bootstrapper to wire TLS
        // termination into it.
        var scratch = await dinD.CreateScratchAsync(
            nameof(PublishWithIncludeWebHttpOnlyEmitsServiceWithoutTlsWiring),
            IncludeWebHttpOnlyConfigPath);

        var publish = await dinD.RunBootstrapperAsync(
            nameof(PublishWithIncludeWebHttpOnlyEmitsServiceWithoutTlsWiring),
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);
        await Assert.That(publish.ExitCode).IsEqualTo(0).Because(publish.Stderr);

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        // The service must be present — that's the whole point of the flag.
        await Assert.That(compose).Contains("octocon-web")
            .Because("octocon-web service must be present when deployment.includeWeb=true even without webHttps");

        // The HTTP-only healthcheck path: the AppHost emits `curl http://localhost:8080/` for
        // the no-TLS variant because the upstream wasm image's nginx listens ONLY on :8080.
        await Assert.That(compose).Contains("http://localhost:8080/")
            .Because("HTTP-only octocon-web must healthcheck the upstream image's :8080 listener");

        // None of the TLS-only bits should appear — bind mounts, NGINX_* env vars, or the HTTPS
        // healthcheck variant. If any of these slip in the operator gets a half-configured
        // stack that either fails to start (mount source missing) or trips a TLS handshake
        // failure on the healthcheck.
        await Assert.That(compose).DoesNotContain("/etc/nginx/templates/default.conf.template")
            .Because("HTTP-only variant must NOT bind-mount the nginx envsubst template");
        await Assert.That(compose).DoesNotContain("NGINX_SERVER_NAME")
            .Because("HTTP-only variant must NOT emit NGINX_* env vars");
        await Assert.That(compose).DoesNotContain("NGINX_HTTPS_PORT_SUFFIX")
            .Because("HTTP-only variant has no redirect to stitch a port suffix into");
        await Assert.That(compose).DoesNotContain("https://localhost:443/")
            .Because("HTTP-only variant must NOT emit the HTTPS healthcheck variant");

        // Operator host ports map onto the upstream image's :8080 listener. The webHttps host
        // port stays bound (for symmetry with the webTls branch's resource-graph shape) but
        // also lands on :8080 — both endpoint names serve plaintext HTTP in this mode.
        await Assert.That(compose).Contains($"\"{scratch.Ports.WebHttp}:8080\"")
            .Because($"compose must publish the allocated webHttp host port ({scratch.Ports.WebHttp}) onto the upstream image's :8080");
    }
}
