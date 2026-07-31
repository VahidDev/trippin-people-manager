using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Domain.Common;
using TripPin.Domain.People;

namespace TripPin.Application.People.Ports;

/// <summary>
/// The single port to the People entity set. Implemented in Infrastructure by
/// the OData client, and decorated there for caching.
/// </summary>
public interface IPeopleRepository
{
    Task<Result<PagedResult<PersonSummary>>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<Result<PagedResult<PersonSummary>>> SearchAsync(
        PersonFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Reads a person for display. Safe to serve from cache.</summary>
    Task<Result<Person>> GetByIdAsync(UserName userName, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a person for the purpose of updating it, always bypassing any
    /// cache, and so returns a current <see cref="ConcurrencyToken"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetByIdAsync"/> on purpose. A cached entity
    /// carries a cached ETag, and a stale ETag produces a 412 on write. A
    /// stale list is cosmetic; a stale token is a failed update. See
    /// docs/adr/ADR-002.
    /// </remarks>
    Task<Result<Person>> GetForUpdateAsync(UserName userName, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a sparse update and returns the person's new token.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="PersonUpdate"/> rather than a whole entity so the
    /// token travels from the caller's own read. Re-reading here to assemble a
    /// full entity would fetch a fresh token and defeat conflict detection
    /// entirely.
    /// </remarks>
    Task<Result<ConcurrencyToken>> UpdateAsync(
        PersonUpdate update,
        CancellationToken cancellationToken);
}
