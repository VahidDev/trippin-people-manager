using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;
using TripPin.Domain.Common;

namespace TripPin.Domain.People;

/// <summary>
/// A validated email address.
/// </summary>
/// <remarks>
/// The service performs no validation on this collection whatsoever: it
/// accepts "not-an-email" with a 204. All meaningful validation is therefore
/// client-side, which is what makes this value object load-bearing rather
/// than decorative.
/// <para>
/// Parsing is delegated to <see cref="MailAddress"/> (part of the shared
/// framework, so the Domain still carries no package references) rather than
/// to a hand-written regular expression. Two checks are layered on top,
/// because MailAddress alone is more permissive than we want: the parsed
/// address must equal the input, which rejects the display-name form
/// <c>Foo &lt;a@b.com&gt;</c>, and the host must be dotted, which rejects
/// <c>a@b</c>.
/// </para>
/// </remarks>
public sealed record EmailAddress
{
    public const int MaxLength = 254;

    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static EmailAddress From(string? value)
    {
        if (TryFrom(value, out var result, out var error))
        {
            return result;
        }

        throw new DomainException(error);
    }

    public static bool TryFrom(
        string? value,
        [NotNullWhen(true)] out EmailAddress? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Email address is required.";
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            error = $"Email address must be {MaxLength} characters or fewer.";
            return false;
        }

        if (!MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal))
        {
            error = $"'{trimmed}' is not a valid email address.";
            return false;
        }

        var host = parsed.Host;

        if (!host.Contains('.', StringComparison.Ordinal)
            || host.StartsWith('.')
            || host.EndsWith('.'))
        {
            error = $"'{trimmed}' is not a valid email address.";
            return false;
        }

        result = new EmailAddress(trimmed);
        return true;
    }

    public override string ToString() => Value;
}
