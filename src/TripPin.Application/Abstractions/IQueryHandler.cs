namespace TripPin.Application.Abstractions;

/// <summary>
/// A read-side use case. CQRS-lite: two hand-rolled interfaces in place of a
/// mediator, resolved directly by the DI container (see docs/adr/ADR-003).
/// </summary>
/// <remarks>
/// The <see cref="CancellationToken"/> is a required parameter, not an
/// optional one with a default. A defaulted token is how cancellation
/// quietly stops working.
/// </remarks>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
