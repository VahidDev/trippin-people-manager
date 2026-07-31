using System.Diagnostics.CodeAnalysis;
using TripPin.Domain.Common;

namespace TripPin.Domain.People;

/// <summary>
/// A person's given and family name, both mandatory.
/// </summary>
/// <remarks>
/// The service metadata marks FirstName and LastName as non-nullable but does
/// not enforce it: an update omitting LastName is accepted with a 204. This
/// type is where that constraint is actually enforced.
/// <para>
/// <see cref="TryNormalizePart"/> is public because an update is sparse per
/// field: a caller may change only the first name, so the Application layer
/// validates each part independently and must not have to restate the rule.
/// </para>
/// </remarks>
public sealed record PersonName
{
    public const int MaxPartLength = 100;

    private PersonName(string first, string last)
    {
        First = first;
        Last = last;
    }

    public string First { get; }

    public string Last { get; }

    public static PersonName From(string? first, string? last)
    {
        if (TryFrom(first, last, out var result, out var error))
        {
            return result;
        }

        throw new DomainException(error);
    }

    public static bool TryFrom(
        string? first,
        string? last,
        [NotNullWhen(true)] out PersonName? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;

        if (!TryNormalizePart(first, "First name", out var normalizedFirst, out error))
        {
            return false;
        }

        if (!TryNormalizePart(last, "Last name", out var normalizedLast, out error))
        {
            return false;
        }

        result = new PersonName(normalizedFirst, normalizedLast);
        error = null;
        return true;
    }

    public static bool TryNormalizePart(
        string? value,
        string fieldName,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{fieldName} is required.";
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxPartLength)
        {
            error = $"{fieldName} must be {MaxPartLength} characters or fewer.";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public override string ToString() => $"{First} {Last}";
}
