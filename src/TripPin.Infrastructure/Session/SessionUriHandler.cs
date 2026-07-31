using Microsoft.Extensions.Options;
using TripPin.Infrastructure.Configuration;

namespace TripPin.Infrastructure.Session;

/// <summary>
/// Rewrites outgoing request URIs onto the resolved session base.
/// </summary>
/// <remarks>
/// Registered inside the resilience handler, so a retried request re-evaluates
/// the session rather than replaying a dead one. The repository therefore only
/// ever builds paths such as <c>People('russellwhyte')</c> and stays unaware
/// that sessions exist at all.
/// <para>
/// The rewrite works on absolute URIs because <see cref="HttpClient"/> has
/// already resolved the request against its <c>BaseAddress</c> by the time any
/// handler runs. The configured base is stripped and the remainder reattached
/// to the session base, which keeps query strings and quoted key segments
/// intact.
/// </para>
/// </remarks>
public sealed class SessionUriHandler(
    ISessionUriProvider sessionUriProvider,
    IOptions<TripPinOptions> options) : DelegatingHandler
{
    private readonly ISessionUriProvider _sessionUriProvider = sessionUriProvider;
    private readonly TripPinOptions _options = options.Value;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not null)
        {
            var sessionBase = await _sessionUriProvider
                .GetBaseAddressAsync(cancellationToken)
                .ConfigureAwait(false);

            request.RequestUri = Rebase(request.RequestUri, _options.BaseAddress, sessionBase);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static Uri Rebase(Uri requestUri, Uri configuredBase, Uri sessionBase)
    {
        if (!requestUri.IsAbsoluteUri)
        {
            return new Uri(sessionBase, requestUri);
        }

        // Already pointing at the session, for instance a retry of a request
        // this handler rewrote on an earlier attempt.
        if (sessionBase.IsBaseOf(requestUri))
        {
            return requestUri;
        }

        // Anything outside the configured service is left alone rather than
        // being forced onto the session base.
        if (!configuredBase.IsBaseOf(requestUri))
        {
            return requestUri;
        }

        return new Uri(sessionBase, configuredBase.MakeRelativeUri(requestUri));
    }
}
