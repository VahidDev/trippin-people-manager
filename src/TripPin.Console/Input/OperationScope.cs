namespace TripPin.Console.Input;

/// <summary>
/// Owns the cancellation lifetime of a single user-triggered operation.
/// </summary>
/// <remarks>
/// Composes three sources into one linked token: process-wide shutdown from
/// Ctrl+C, a per-operation timeout, and supersession. Supersession gives
/// overlapping searches last-one-wins semantics, so a slow first search cannot
/// land after and overwrite a fast second one.
/// <para>
/// This type is watched deliberately. If the supersede or linking logic grows
/// beyond what is here, it stops being incidental console plumbing and earns
/// its own tests, per the agreed plan. That is the trigger for adding a test
/// project or folding these tests into an existing one.
/// </para>
/// </remarks>
public sealed class OperationScope : IDisposable
{
    private readonly CancellationToken _applicationStopping;

    private CancellationTokenSource? _current;

    public OperationScope(CancellationToken applicationStopping) =>
        _applicationStopping = applicationStopping;

    /// <summary>
    /// Begins an operation, cancelling any operation already in flight.
    /// </summary>
    /// <remarks>
    /// The timeout is a backstop above the per-request HttpClient timeout, not
    /// a substitute for it: a list read is two requests, so this has to allow
    /// for more than one round trip.
    /// </remarks>
    public CancellationToken Begin(TimeSpan timeout)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
        next.CancelAfter(timeout);

        Supersede(Interlocked.Exchange(ref _current, next));

        return next.Token;
    }

    /// <summary>Cancels the in-flight operation, if any, without starting another.</summary>
    public void CancelCurrent() => Supersede(Interlocked.Exchange(ref _current, null));

    public void Dispose() => CancelCurrent();

    /// <summary>
    /// Cancels then releases a superseded source.
    /// </summary>
    /// <remarks>
    /// Disposal immediately after cancelling is safe here because the menu loop
    /// awaits each action to completion, so nothing is still holding the token
    /// by the time the next action begins. Supersession is defensive, for the
    /// day an overlapping request path is added.
    /// </remarks>
    private static void Supersede(CancellationTokenSource? previous)
    {
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
            previous.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already released by a concurrent supersede.
        }
    }
}
