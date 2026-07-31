using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using TripPin.Infrastructure.Configuration;
using TripPin.Infrastructure.OData;

namespace TripPin.Infrastructure.Resilience;

/// <summary>
/// Configures retry-with-jitter and a circuit breaker on the OData client.
/// </summary>
/// <remarks>
/// Retries cover 5xx, 408, 429 (honouring Retry-After), network faults and
/// timeouts. 412 and 428 are deliberately excluded from both the retry
/// predicate and the breaker: a 412 means the ETag was stale and a replay sends
/// the same stale value, while a 428 means we failed to send If-Match at all,
/// which is a bug in our own code rather than a service fault. 400 is excluded
/// for the same reason, since a malformed request stays malformed.
/// <para>
/// Both strategies share one predicate, which is what makes the exclusion hold
/// for the breaker too: Polly counts only the outcomes a strategy handles, so
/// an unhandled 412 registers as a success and cannot push the circuit open
/// against a service that is behaving perfectly well.
/// </para>
/// <para>
/// Retry is added before the breaker, making it the outer of the two, so a
/// burst of retries against a failing service still feeds the breaker.
/// </para>
/// </remarks>
public static class ResiliencePipelineSetup
{
    public const string PipelineName = "TripPin.OData";

    public static IHttpClientBuilder AddTripPinResilience(
        this IHttpClientBuilder builder,
        ResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.AddResilienceHandler(PipelineName, (pipeline, context) =>
        {
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(PipelineName);

            // Polly rejects a zero retry count outright, so zero is honoured
            // here as "retries disabled" rather than being allowed to become a
            // startup crash for anyone who sets it in configuration.
            if (options.MaxRetryAttempts > 0)
            {
                pipeline.AddRetry(BuildRetryOptions(options, logger));
            }

            pipeline.AddCircuitBreaker(BuildCircuitBreakerOptions(options, logger));
        });

        return builder;
    }

    private static HttpRetryStrategyOptions BuildRetryOptions(
        ResilienceOptions options,
        ILogger logger) =>
        new()
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(options.BaseDelaySeconds),
            ShouldRetryAfterHeader = true,
            ShouldHandle = arguments => ValueTask.FromResult(
                ShouldTreatAsTransient(arguments.Outcome, arguments.Context.CancellationToken)),
            OnRetry = arguments =>
            {
                logger.LogWarning(
                    "Retrying OData request, attempt {AttemptNumber} after {RetryDelay}. Outcome: {Outcome}.",
                    arguments.AttemptNumber + 1,
                    arguments.RetryDelay,
                    Describe(arguments.Outcome));

                return default;
            },
        };

    private static HttpCircuitBreakerStrategyOptions BuildCircuitBreakerOptions(
        ResilienceOptions options,
        ILogger logger) =>
        new()
        {
            FailureRatio = 0.5,
            MinimumThroughput = options.CircuitBreakerMinimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
            ShouldHandle = arguments => ValueTask.FromResult(
                ShouldTreatAsTransient(arguments.Outcome, arguments.Context.CancellationToken)),
            OnOpened = arguments =>
            {
                logger.LogError("OData circuit opened for {BreakDuration}.", arguments.BreakDuration);

                return default;
            },
            OnClosed = _ =>
            {
                logger.LogInformation("OData circuit closed.");

                return default;
            },
        };

    /// <summary>
    /// Decides whether an outcome is worth another attempt.
    /// </summary>
    /// <remarks>
    /// The cancellation case needs care. An <see cref="HttpClient"/> timeout
    /// surfaces as a <see cref="TaskCanceledException"/>, which is an
    /// <see cref="OperationCanceledException"/> and therefore indistinguishable
    /// by type from the caller abandoning the request. They must be treated
    /// oppositely: a timeout is exactly the transient fault retries exist for,
    /// while retrying after the caller gave up wastes attempts on a result
    /// nobody will read. The caller's own token separates them.
    /// </remarks>
    private static bool ShouldTreatAsTransient(
        Outcome<HttpResponseMessage> outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.Exception is not null)
        {
            return outcome.Exception switch
            {
                HttpRequestException => true,
                TimeoutException => true,
                OperationCanceledException => !cancellationToken.IsCancellationRequested,
                _ => false,
            };
        }

        return outcome.Result is not null
            && ODataStatusInterpreter.IsTransient(outcome.Result.StatusCode);
    }

    private static string Describe(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception?.GetType().Name
        ?? outcome.Result?.StatusCode.ToString()
        ?? "unknown";
}
