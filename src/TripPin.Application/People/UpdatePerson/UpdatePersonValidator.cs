using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Domain.People;

namespace TripPin.Application.People.UpdatePerson;

/// <summary>
/// Turns an <see cref="UpdatePersonCommand"/> into a validated
/// <see cref="PersonUpdate"/>, or into the list of reasons it cannot.
/// </summary>
/// <remarks>
/// Hand-rolled rather than FluentValidation. The rules themselves already
/// live in the Domain value objects, so a rule DSL here would either restate
/// them or wrap them; what this layer actually adds is collecting messages
/// instead of throwing on the first failure, which is the loop below.
/// <para>
/// Static and dependency-free, so it is callable directly from a test with no
/// container, and reachable from the handler with no registration.
/// </para>
/// </remarks>
public static class UpdatePersonValidator
{
    public static Result<PersonUpdate> Validate(UpdatePersonCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<string>();

        var firstName = NormalizeOptionalPart(command.FirstName, "First name", errors);
        var lastName = NormalizeOptionalPart(command.LastName, "Last name", errors);
        var emails = ValidateOptionalEmails(command.Emails, errors);
        var gender = ValidateOptionalGender(command.Gender, errors);

        var update = new PersonUpdate
        {
            UserName = command.UserName,
            Concurrency = command.Concurrency,
            FirstName = firstName,
            LastName = lastName,
            Emails = emails,
            Gender = gender,
        };

        // Checked after field validation so a command that is both empty and
        // malformed reports the malformed fields rather than only "no changes".
        if (errors.Count == 0 && !update.HasChanges)
        {
            errors.Add("No changes were supplied.");
        }

        return errors.Count > 0
            ? Result<PersonUpdate>.ValidationFailed(errors)
            : Result<PersonUpdate>.Success(update);
    }

    private static string? NormalizeOptionalPart(
        string? value,
        string fieldName,
        List<string> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (!PersonName.TryNormalizePart(value, fieldName, out var normalized, out var error))
        {
            errors.Add(error);
            return null;
        }

        return normalized;
    }

    private static List<EmailAddress>? ValidateOptionalEmails(
        IReadOnlyList<string>? emails,
        List<string> errors)
    {
        if (emails is null)
        {
            return null;
        }

        // An empty list is valid and meaningful: it clears the collection.
        // It must stay distinguishable from null all the way to the wire.
        var validated = new List<EmailAddress>(emails.Count);

        for (var index = 0; index < emails.Count; index++)
        {
            if (EmailAddress.TryFrom(emails[index], out var email, out var error))
            {
                validated.Add(email);
            }
            else
            {
                errors.Add($"Emails[{index}]: {error}");
            }
        }

        return validated;
    }

    private static Gender? ValidateOptionalGender(Gender? gender, List<string> errors)
    {
        if (gender is null)
        {
            return null;
        }

        if (!Enum.IsDefined(gender.Value))
        {
            errors.Add($"'{(int)gender.Value}' is not a valid gender.");
            return null;
        }

        return gender;
    }
}
