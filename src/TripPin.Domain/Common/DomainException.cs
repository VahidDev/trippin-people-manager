namespace TripPin.Domain.Common;

/// <summary>
/// Raised when a domain invariant is violated. Never used for transport,
/// availability or concurrency concerns, which are modelled as results.
/// </summary>
/// <remarks>
/// Every value object exposes a throwing <c>From</c> and a non-throwing
/// <c>TryFrom</c>. <c>From</c> is for values the system controls and a failure
/// is a bug. <c>TryFrom</c> is for user input, where the Application layer
/// collects messages into a validation result instead of throwing.
/// </remarks>
public sealed class DomainException : Exception
{
    public DomainException()
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
