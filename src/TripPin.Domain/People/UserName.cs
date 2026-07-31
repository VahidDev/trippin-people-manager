using System.Diagnostics.CodeAnalysis;
using TripPin.Domain.Common;

namespace TripPin.Domain.People;

/// <summary>
/// The identity of a <see cref="Person"/>, and the entity key.
/// </summary>
/// <remarks>
/// Immutable by contract: the service accepts an update that changes it,
/// returns 204, and silently ignores the change. Modelling it as a read-only
/// value object keeps that trap out of reach of the edit screen.
/// </remarks>
public sealed record UserName
{
    public const int MaxLength = 100;

    private UserName(string value) => Value = value;

    public string Value { get; }

    public static UserName From(string? value)
    {
        if (TryFrom(value, out var result, out var error))
        {
            return result;
        }

        throw new DomainException(error);
    }

    public static bool TryFrom(
        string? value,
        [NotNullWhen(true)] out UserName? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "User name is required.";
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            error = $"User name must be {MaxLength} characters or fewer.";
            return false;
        }

        result = new UserName(trimmed);
        return true;
    }

    public override string ToString() => Value;
}
