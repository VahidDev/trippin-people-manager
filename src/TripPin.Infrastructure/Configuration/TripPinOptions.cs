using System.ComponentModel.DataAnnotations;

namespace TripPin.Infrastructure.Configuration;

/// <summary>
/// Bound from the "TripPin" section of appsettings.json and validated at startup.
/// </summary>
public sealed class TripPinOptions
{
    public const string SectionName = "TripPin";

    /// <summary>
    /// The service root. Note this is the pre-redirect address: the service
    /// answers with a 302 to a session-scoped URL, which is resolved once at
    /// startup and reused. See <c>Session/SessionUriProvider</c>.
    /// </summary>
    /// <remarks>
    /// The default is intentional, and there is deliberately no
    /// <c>[Required]</c> alongside it. This application targets one fixed
    /// public sample service, so a built-in address is the correct value
    /// rather than a placeholder, and the app stays runnable if the section is
    /// absent. <c>[Required]</c> on a property that always holds a value is
    /// dead metadata that reads as enforcement while enforcing nothing.
    /// <para>
    /// The risk a default carries is masking a configuration file that was
    /// never loaded. That is addressed at the source: <c>Program</c> roots
    /// configuration at the binary rather than the working directory, and the
    /// startup banner prints the address actually in use, so a fallback is
    /// visible rather than silent.
    /// </para>
    /// </remarks>
    public Uri BaseAddress { get; set; } = new("https://services.odata.org/v4/TripPinServiceRW/");

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Range(1, 100)]
    public int PageSize { get; set; } = 8;

    public CacheOptions Cache { get; set; } = new();

    public ResilienceOptions Resilience { get; set; } = new();
}

/// <summary>TTLs for the read-side cache. Writes invalidate explicitly.</summary>
public sealed class CacheOptions
{
    public bool Enabled { get; set; } = true;

    [Range(0, 3600)]
    public int ListTtlSeconds { get; set; } = 60;

    [Range(0, 3600)]
    public int DetailTtlSeconds { get; set; } = 120;

    /// <summary>Bounds cache growth across many distinct query shapes.</summary>
    [Range(1, 10000)]
    public int SizeLimit { get; set; } = 256;
}

/// <summary>
/// Retry and circuit-breaker settings for the OData client.
/// </summary>
/// <remarks>
/// 412 and 428 are explicitly not retryable and must not count toward the
/// breaker: both are semantic outcomes, not transient faults. Retrying a 412
/// resends the same stale ETag and fails identically.
/// </remarks>
public sealed class ResilienceOptions
{
    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(1, 60)]
    public int BaseDelaySeconds { get; set; } = 1;

    [Range(1, 100)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    [Range(1, 600)]
    public int CircuitBreakerDurationSeconds { get; set; } = 30;
}
