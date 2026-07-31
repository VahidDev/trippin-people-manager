using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripPin.Infrastructure.Configuration;

namespace TripPin.Infrastructure.Session;

/// <summary>
/// Singleton implementation backed by a lazily-initialised shared task.
/// </summary>
/// <remarks>
/// N concurrent callers await one resolution rather than opening N sessions,
/// which matters because every extra resolution mints a session with its own
/// pristine copy of the data and silently discards earlier writes.
/// <para>
/// Two details make the sharing safe. The resolution itself runs on
/// <see cref="CancellationToken.None"/> and callers observe it through
/// <c>WaitAsync</c>, so one caller cancelling cannot tear down the resolution
/// another is waiting on. And a faulted resolution is evicted rather than
/// cached, so a transient network failure at startup does not poison the
/// provider for the lifetime of the process.
/// </para>
/// </remarks>
public sealed class SessionUriProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<TripPinOptions> options,
    ILogger<SessionUriProvider> logger) : ISessionUriProvider
{
    /// <summary>Named client used only for the one-off redirect resolution.</summary>
    public const string ResolverClientName = "TripPin.SessionResolver";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly TripPinOptions _options = options.Value;
    private readonly ILogger<SessionUriProvider> _logger = logger;
    private readonly Lock _gate = new();

    private Lazy<Task<Uri>>? _resolution;

    public async Task<Uri> GetBaseAddressAsync(CancellationToken cancellationToken)
    {
        var pending = Current();

        try
        {
            return await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Evict(pending);
            throw;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _resolution = null;
        }

        _logger.LogInformation("Session base address invalidated; it will be resolved again.");
    }

    private Lazy<Task<Uri>> Current()
    {
        lock (_gate)
        {
            return _resolution ??= new Lazy<Task<Uri>>(
                ResolveAsync,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    /// <summary>Clears the cache only if it still holds the failed attempt.</summary>
    private void Evict(Lazy<Task<Uri>> failed)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_resolution, failed))
            {
                _resolution = null;
            }
        }
    }

    private async Task<Uri> ResolveAsync()
    {
        var configured = EnsureTrailingSlash(_options.BaseAddress);

        _logger.LogDebug("Resolving the session base address from {ConfiguredBase}.", configured);

        var client = _httpClientFactory.CreateClient(ResolverClientName);

        using var response = await client
            .GetAsync(configured, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // The service answers the bare root with a 302 to a session-scoped
        // address, so the final request URI is the session base. The resolver
        // client follows redirects for exactly this reason.
        var resolved = EnsureTrailingSlash(response.RequestMessage?.RequestUri ?? configured);

        _logger.LogInformation("Session base address resolved to {SessionBase}.", resolved);

        return resolved;
    }

    /// <summary>
    /// A missing trailing slash would make relative resolution drop the last
    /// path segment, silently addressing the wrong service.
    /// </summary>
    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/");
}
