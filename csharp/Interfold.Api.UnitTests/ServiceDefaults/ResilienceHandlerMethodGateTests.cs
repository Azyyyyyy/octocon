using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace Interfold.Api.UnitTests.ServiceDefaults;

/// <summary>
/// Pins the contract of <see cref="Extensions.ConfigureResilienceForGetOnly"/> — the GET-only
/// gate we put around the standard <c>Microsoft.Extensions.Http.Resilience</c> pipeline so the
/// duplicate-SP-import bug class can never reappear.
///
/// <para>
/// <b>The bug this guards against.</b> The default <c>AddStandardResilienceHandler</c> retries
/// on <c>TimeoutRejectedException</c> (10 s default attempt timeout). On the Pi 4 production
/// host an SP import POST through the in-process loopback HttpClient takes ~10 s end to end,
/// which races the attempt timeout. Polly cancelled the in-flight call and retried — three
/// times. Each retry minted a fresh idempotency key in the controller's
/// <c>GetIdempotencyKey</c> fallback, so the per-command dedupe missed and the importer ran
/// from scratch each time, producing 2-3× row sets per click. See the captured Polly[0]
/// OnTimeout + TimeoutRejectedException sequence in <c>scripts/diagnostics/raspi_dump.txt</c>.
/// </para>
///
/// <para>
/// <b>The fix invariant.</b> Retrying state-changing verbs (POST/PUT/PATCH/DELETE) is unsafe
/// in general — the server can't distinguish a retry from a fresh client intent without an
/// out-of-band idempotency contract. GETs are idempotent and safe to retry. The
/// configurer therefore gates both <see cref="HttpStandardResilienceOptions.Retry"/>'s
/// <c>ShouldHandle</c> and <see cref="HttpStandardResilienceOptions.CircuitBreaker"/>'s
/// <c>ShouldHandle</c> on <c>request.Method == HttpMethod.Get</c>. These tests pin that
/// behaviour through a real HttpClient stood up with the same configurer the production
/// pipeline uses.
/// </para>
/// </summary>
public sealed class ResilienceHandlerMethodGateTests
{
    /// <summary>
    /// Pins: a GET that the transport handles with a 503 is retried by Polly until it
    /// gets a 200 (or exhausts the retry budget). This is the standard transient-handling
    /// behaviour that we want to preserve for idempotent reads.
    /// </summary>
    [Test]
    public async Task GetReceiving503ThenOk_IsRetriedAndSucceeds()
    {
        var counter = new RequestCountingHandler(
            (request, attempt) => attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK));

        using var client = BuildHttpClient(counter);

        using var response = await client.GetAsync("https://example.test/probe", CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because("A GET with a transient 503 followed by a 200 should bubble up the eventual 200 — that's the point of retaining the standard pipeline for GETs.");
            await Assert.That(counter.AttemptCount).IsGreaterThanOrEqualTo(2)
                .Because("Polly must have made at least one retry attempt after the 503; otherwise the GET path has lost its safety net.");
        }
    }

    /// <summary>
    /// Pins: a POST that the transport handles with a 503 is NOT retried. The first 503
    /// surfaces to the caller as-is and the caller can decide what to do (typically: show
    /// the user an error rather than silently re-submitting state-changing work).
    /// </summary>
    [Test]
    public async Task PostReceiving503_IsNotRetried()
    {
        var counter = new RequestCountingHandler(
            (request, attempt) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var client = BuildHttpClient(counter);

        using var response = await client.PostAsync(
            "https://example.test/mutation",
            new StringContent("{}"),
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable)
                .Because("The first 503 must surface to the caller unchanged — Polly retries on POST were the proximate cause of the duplicate-SP-import bug.");
            await Assert.That(counter.AttemptCount).IsEqualTo(1)
                .Because("A POST must be sent exactly once. Any retry here means the GET-only gate has regressed and we're back to the duplicate-state risk.");
        }
    }

    /// <summary>
    /// Pins the same invariant across the other state-changing verbs. PUT, PATCH, DELETE
    /// share POST's unsafe-to-retry property (their effect on the server is path-dependent
    /// and not idempotent unless the server promises otherwise).
    /// </summary>
    [Test]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    [Arguments("DELETE")]
    public async Task NonGetMutationVerbReceiving503_IsNotRetried(string methodName)
    {
        var counter = new RequestCountingHandler(
            (request, attempt) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var client = BuildHttpClient(counter);

        using var request = new HttpRequestMessage(new HttpMethod(methodName), "https://example.test/mutation");

        using var response = await client.SendAsync(request, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable)
                .Because($"{methodName} responses must not be retried — the GET-only gate covers every non-idempotent verb, not just POST.");
            await Assert.That(counter.AttemptCount).IsEqualTo(1)
                .Because($"{methodName} must be sent exactly once. Retries on this verb create the same duplicate-state risk POST does.");
        }
    }

    /// <summary>
    /// Pins the configured timeout ceilings. These values are part of the public contract
    /// of <see cref="Extensions.ConfigureResilienceForGetOnly"/> — they cap how long any
    /// request (GET or otherwise) can hold a connection before the pipeline gives up,
    /// regardless of the upstream caller's own cancellation token. They were set deliberately
    /// (30 s per attempt, 2 min total) so the SP import — which the user reports takes ~10 s
    /// on Pi 4 + Cassandra — has comfortable headroom, but a genuinely stuck downstream
    /// can't pin a connection indefinitely.
    /// </summary>
    [Test]
    public async Task TimeoutOptions_AreConfiguredToTheDocumentedCeilings()
    {
        var options = new HttpStandardResilienceOptions();
        Extensions.ConfigureResilienceForGetOnly(options);

        using (Assert.Multiple())
        {
            await Assert.That(options.AttemptTimeout.Timeout).IsEqualTo(TimeSpan.FromSeconds(30))
                .Because("AttemptTimeout pins the per-attempt ceiling. Bumping it from the package default of 10 s is what made the SP import safe even without the async-job refactor.");
            await Assert.That(options.TotalRequestTimeout.Timeout).IsEqualTo(TimeSpan.FromMinutes(2))
                .Because("TotalRequestTimeout caps the whole pipeline including any GET retries. 2 min is the operational ceiling we agreed on.");
            await Assert.That(options.CircuitBreaker.SamplingDuration).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(60))
                .Because("Microsoft.Extensions.Http.Resilience validates SamplingDuration >= 2 * AttemptTimeout — with AttemptTimeout = 30 s this must be >= 60 s or the host fails to start.");
        }
    }

    /// <summary>
    /// Stands up a minimal DI container with an HttpClient configured the same way the
    /// production <see cref="Extensions.AddServiceDefaults{TBuilder}"/> wires it. The
    /// transport is the supplied <paramref name="primaryHandler"/> so the test can
    /// observe attempt counts and force the responses the pipeline reacts to.
    /// </summary>
    private static HttpClient BuildHttpClient(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("test")
            .AddStandardResilienceHandler().Configure(Extensions.ConfigureResilienceForGetOnly);

        // Replace the primary handler AFTER the resilience handler so the resilience
        // pipeline wraps the test transport. ConfigurePrimaryHttpMessageHandler is the
        // factory hook for the innermost handler — exactly the spot we want.
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        return factory.CreateClient("test");
    }

    /// <summary>
    /// In-memory transport that increments an attempt counter and returns whatever the
    /// supplied factory dictates. Lets each test case control how the transport responds
    /// per attempt while observing the exact number of times Polly invoked it.
    /// </summary>
    private sealed class RequestCountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responseFactory;
        private int _attempts;

        public RequestCountingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int AttemptCount => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            var response = _responseFactory(request, attempt);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
