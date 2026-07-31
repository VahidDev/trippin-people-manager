using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;
using TripPin.Application.Abstractions;
using TripPin.Infrastructure.Configuration;
using TripPin.Infrastructure.OData;
using TripPin.Infrastructure.Resilience;
using Xunit;

namespace TripPin.Infrastructure.Tests.QuirkHandling;

/// <summary>
/// Exercises the real Polly pipeline rather than a bare stubbed client.
/// </summary>
/// <remarks>
/// Every other test in this project wires <c>mockHttp.ToHttpClient()</c>
/// directly, which bypasses resilience entirely. These build the client through
/// <see cref="ResiliencePipelineSetup"/>, so retry and circuit-breaker
/// behaviour is genuinely under test.
/// <para>
/// Delays are real, so attempt counts are kept low deliberately: the point is
/// which outcomes are retried, not how long the backoff waits.
/// </para>
/// </remarks>
public sealed class ResiliencePipelineTests
{
    private const string BaseUrl = "https://example.test/svc/";
    private const string CollectionUrl = "https://example.test/svc/People";
    private const string DetailUrl = "https://example.test/svc/People('russellwhyte')";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Keeps the provider alive for as long as the client is used.</summary>
    private sealed class Harness(ServiceProvider provider, HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose() => provider.Dispose();
    }

    private static Harness Wrap(
        MockHttpMessageHandler mockHttp,
        int retries = 1,
        int minimumThroughput = 100)
    {
        var options = new ResilienceOptions
        {
            MaxRetryAttempts = retries,
            BaseDelaySeconds = 1,

            // Effectively disables the breaker unless a test asks for it, so
            // retry behaviour can be observed on its own.
            CircuitBreakerMinimumThroughput = minimumThroughput,
            CircuitBreakerDurationSeconds = 1,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("test", client => client.BaseAddress = new Uri(BaseUrl))
            .AddTripPinResilience(options)
            .ConfigurePrimaryHttpMessageHandler(() => mockHttp);

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        return new Harness(provider, client);
    }

    private static ODataPeopleRepository RepositoryOver(HttpClient client) =>
        new(
            client,
            new ODataFilterTranslator(),
            new PersonMapper(NullLogger<PersonMapper>.Instance),
            new ODataStatusInterpreter(),
            Options.Create(new TripPinOptions()),
            NullLogger<ODataPeopleRepository>.Instance);

    // -----------------------------------------------------------------
    // Retried outcomes
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_500_is_retried()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        using var harness = Wrap(mockHttp, retries: 2);
        using var response = await harness.Client.GetAsync(new Uri(CollectionUrl), Token);

        attempts.Should().Be(3, "one initial attempt plus two retries");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Every_transient_status_is_retried(HttpStatusCode statusCode)
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            return new HttpResponseMessage(statusCode);
        });

        using var harness = Wrap(mockHttp);
        using var response = await harness.Client.GetAsync(new Uri(CollectionUrl), Token);

        attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_recovered_request_returns_the_successful_response()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : StubbedService.Json(StubbedService.CollectionJson);
        });

        using var harness = Wrap(mockHttp);
        using var response = await harness.Client.GetAsync(new Uri(CollectionUrl), Token);

        attempts.Should().Be(2);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_network_fault_is_retried()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            throw new HttpRequestException("connection reset");
        });

        using var harness = Wrap(mockHttp);
        var act = async () => await harness.Client.GetAsync(new Uri(CollectionUrl), Token);

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(2);
    }

    /// <summary>
    /// The case a status-only predicate misses.
    /// </summary>
    /// <remarks>
    /// An <see cref="HttpClient"/> timeout does not arrive as a status code or
    /// as an <see cref="HttpRequestException"/>. It arrives as a
    /// <see cref="TaskCanceledException"/> wrapping a
    /// <see cref="TimeoutException"/>, which is the same type the runtime uses
    /// when a caller abandons a request. A predicate that only matched
    /// <c>HttpRequestException</c> would silently never retry the single most
    /// common transient fault there is.
    /// </remarks>
    [Fact]
    public async Task A_client_timeout_is_retried_rather_than_surfacing_immediately()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException());
        });

        using var harness = Wrap(mockHttp, retries: 2);
        var act = async () => await harness.Client.GetAsync(new Uri(CollectionUrl), Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
        attempts.Should().Be(3, "a timeout is exactly the fault retries exist for");
    }

    [Fact]
    public async Task A_bare_timeout_exception_is_retried()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            throw new TimeoutException("timed out");
        });

        using var harness = Wrap(mockHttp);
        var act = async () => await harness.Client.GetAsync(new Uri(CollectionUrl), Token);

        await act.Should().ThrowAsync<TimeoutException>();
        attempts.Should().Be(2);
    }

    /// <summary>
    /// A timeout must reach the caller as a result, not an escaping exception.
    /// </summary>
    [Fact]
    public async Task A_timeout_reaches_the_repository_as_a_transport_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();

        mockHttp.When(CollectionUrl).Respond(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout.",
            new TimeoutException()));

        using var harness = Wrap(mockHttp);

        var result = await RepositoryOver(harness.Client).ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.TransportFailure);
    }

    // -----------------------------------------------------------------
    // Outcomes that must never be retried
    // -----------------------------------------------------------------

    /// <summary>
    /// Replaying a 412 resends the same stale ETag and fails identically, so a
    /// retry burns the budget for nothing.
    /// </summary>
    [Fact]
    public async Task A_412_is_never_retried()
    {
        await AssertAttemptedOnceAsync(HttpStatusCode.PreconditionFailed);
    }

    /// <summary>428 means our own code omitted If-Match: a defect, not a fault.</summary>
    [Fact]
    public async Task A_428_is_never_retried()
    {
        await AssertAttemptedOnceAsync((HttpStatusCode)428);
    }

    /// <summary>A malformed request stays malformed however many times it is sent.</summary>
    [Fact]
    public async Task A_400_is_never_retried()
    {
        await AssertAttemptedOnceAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Non_transient_statuses_are_not_retried(HttpStatusCode statusCode)
    {
        await AssertAttemptedOnceAsync(statusCode);
    }

    /// <summary>
    /// Retrying after the caller has given up spends attempts on a result
    /// nobody will read, which is why the predicate consults the caller's token
    /// rather than the exception type alone.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        using var mockHttp = new MockHttpMessageHandler();
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            cts.Cancel();
            throw new TaskCanceledException("caller gave up");
        });

        using var harness = Wrap(mockHttp, retries: 3);
        var act = async () => await harness.Client.GetAsync(new Uri(CollectionUrl), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1, "the caller's token was cancelled, so no retry is warranted");
    }

    private static async Task AssertAttemptedOnceAsync(HttpStatusCode statusCode)
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(DetailUrl).Respond(_ =>
        {
            attempts++;
            return new HttpResponseMessage(statusCode);
        });

        using var harness = Wrap(mockHttp, retries: 3);
        using var response = await harness.Client.GetAsync(new Uri(DetailUrl), Token);

        attempts.Should().Be(1);
        response.StatusCode.Should().Be(statusCode);
    }

    // -----------------------------------------------------------------
    // Circuit breaker
    // -----------------------------------------------------------------

    /// <summary>
    /// Sustained 5xx eventually opens the circuit and requests short-circuit
    /// without reaching the service.
    /// </summary>
    [Fact]
    public async Task Sustained_failures_open_the_circuit()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(CollectionUrl).Respond(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        using var harness = Wrap(mockHttp, retries: 0, minimumThroughput: 2);

        for (var i = 0; i < 6; i++)
        {
            try
            {
                using var response = await harness.Client.GetAsync(new Uri(CollectionUrl), Token);
            }
            catch (Exception exception) when (exception.GetType().Name.Contains("BrokenCircuit", StringComparison.Ordinal))
            {
                // Once open, the breaker rejects before the handler runs.
            }
        }

        attempts.Should().BeLessThan(6, "the circuit should stop some requests reaching the service");
    }

    /// <summary>
    /// The exclusion that matters most for the breaker: semantic outcomes must
    /// not count as failures, or ordinary editing conflicts would eventually
    /// take the client offline against a perfectly healthy service.
    /// </summary>
    [Fact]
    public async Task Conflicts_never_open_the_circuit()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(DetailUrl).Respond(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        });

        using var harness = Wrap(mockHttp, retries: 0, minimumThroughput: 2);

        for (var i = 0; i < 20; i++)
        {
            using var response = await harness.Client.GetAsync(new Uri(DetailUrl), Token);
            response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }

        attempts.Should().Be(20, "every request must still reach the service");
    }
}
