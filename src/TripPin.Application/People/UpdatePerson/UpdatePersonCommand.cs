using TripPin.Domain.Common;
using TripPin.Domain.People;

namespace TripPin.Application.People.UpdatePerson;

/// <summary>
/// Edit a person's writable fields.
/// </summary>
/// <remarks>
/// Carries raw strings because it arrives from console input; the validator
/// turns them into domain values. Every optional property is null to mean
/// "leave alone", which is what makes the resulting update sparse.
/// <para>
/// The <see cref="Emails"/> null-versus-empty distinction is deliberate and
/// survives all the way to the wire: null leaves the list untouched, an empty
/// list clears it. See <see cref="Models.PersonUpdate"/>.
/// </para>
/// <para>
/// AddressInfo is absent by design; see docs/adr/ADR-004.
/// </para>
/// </remarks>
public sealed record UpdatePersonCommand
{
    public required UserName UserName { get; init; }

    /// <summary>From the caller's prior read, via GetForUpdateAsync.</summary>
    public required ConcurrencyToken Concurrency { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public IReadOnlyList<string>? Emails { get; init; }

    public Gender? Gender { get; init; }
}
