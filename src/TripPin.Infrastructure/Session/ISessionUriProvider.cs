namespace TripPin.Infrastructure.Session;

/// <summary>
/// Resolves and caches the session-scoped service base address.
/// </summary>
/// <remarks>
/// The service root 302-redirects to a URL of the form
/// <c>.../v4/(S(sessionid))/TripPinServiceRW/</c>, and every fresh hit on the
/// bare root mints a brand new session with pristine data. Resolving per
/// request would therefore discard every write. This is resolved once and
/// shared, which makes it the only piece of long-lived mutable state in
/// the application.
/// </remarks>
public interface ISessionUriProvider
{
    /// <summary>
    /// Returns the session base address, resolving it on first use.
    /// Concurrent callers share a single resolution rather than racing.
    /// </summary>
    Task<Uri> GetBaseAddressAsync(CancellationToken cancellationToken);

    /// <summary>Discards the cached session so the next call re-resolves.</summary>
    void Invalidate();
}
