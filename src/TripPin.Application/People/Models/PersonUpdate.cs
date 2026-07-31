using TripPin.Domain.Common;
using TripPin.Domain.People;

namespace TripPin.Application.People.Models;

/// <summary>
/// A validated, sparse instruction to change a person.
/// </summary>
/// <remarks>
/// Sparse by construction: a null property means "leave this field alone" and
/// is simply absent from the request Infrastructure builds. That is not an
/// optimisation. Emitting a full entity would re-send AddressInfo on every
/// save, and a partial address write corrupts the record beyond repair through
/// the API (see docs/adr/ADR-004).
/// <para>
/// <b>Contract Infrastructure must honour on <see cref="Emails"/>.</b> The
/// three states are distinct and all three are reachable:
/// </para>
/// <list type="bullet">
///   <item><c>null</c> means untouched: omit the property entirely.</item>
///   <item>
///     an empty list means clear: serialise as <c>[]</c>. It must never be
///     serialised as <c>null</c>, which the service answers with a 500.
///   </item>
///   <item>a non-empty list means replace, since PATCH replaces wholesale.</item>
/// </list>
/// <para>
/// <see cref="Gender"/> is nullable here only to express "untouched". It is
/// never sent as a null value: the service accepts that with a 204 and
/// silently coerces to Male.
/// </para>
/// </remarks>
public sealed record PersonUpdate
{
    public required UserName UserName { get; init; }

    /// <summary>
    /// Taken from the caller's own read, not re-fetched. That is what makes a
    /// conflict meaningful: it proves the record changed since the caller
    /// looked at it.
    /// </summary>
    public required ConcurrencyToken Concurrency { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public IReadOnlyList<EmailAddress>? Emails { get; init; }

    public Gender? Gender { get; init; }

    public bool HasChanges =>
        FirstName is not null || LastName is not null || Emails is not null || Gender is not null;
}
