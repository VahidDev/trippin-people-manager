using System.Diagnostics.CodeAnalysis;

namespace TripPin.Domain.Common;

/// <summary>
/// The opaque optimistic-concurrency token for a <see cref="People.Person"/>.
/// </summary>
/// <remarks>
/// Sourced from the service's ETag. The service rejects any update that does
/// not carry one (HTTP 428), so this is a required part of the update path
/// rather than an optimisation. Treated as opaque on purpose: the fact that it
/// happens to derive from the Concurrency property is a service detail the
/// Domain does not model, and the format is not validated because a service
/// change to it should not break reads.
/// </remarks>
public sealed record ConcurrencyToken
{
    private ConcurrencyToken(string value) => Value = value;

    public string Value { get; }

    /// <summary>Token meaning "overwrite regardless of concurrent changes".</summary>
    public static ConcurrencyToken Any { get; } = new("*");

    public static ConcurrencyToken From(string? value)
    {
        if (TryFrom(value, out var result, out var error))
        {
            return result;
        }

        throw new DomainException(error);
    }

    public static bool TryFrom(
        string? value,
        [NotNullWhen(true)] out ConcurrencyToken? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Concurrency token is required.";
            return false;
        }

        result = new ConcurrencyToken(value.Trim());
        return true;
    }

    public override string ToString() => Value;
}
