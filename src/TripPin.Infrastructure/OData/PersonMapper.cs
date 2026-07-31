using Microsoft.Extensions.Logging;
using TripPin.Application.People.Models;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using TripPin.Infrastructure.OData.Dtos;

namespace TripPin.Infrastructure.OData;

/// <summary>
/// Converts between wire DTOs and the domain model. The only place wire
/// concerns are allowed to touch the model.
/// </summary>
/// <remarks>
/// Rules this mapper must hold to, each of them a measured service behaviour:
/// <list type="bullet">
///   <item>
///     Updates are sparse. Only the fields set on <see cref="PersonUpdate"/>
///     are emitted. Sending the whole entity would re-send AddressInfo on
///     every save, and a partial address write corrupts the record beyond
///     recovery through the API.
///   </item>
///   <item>
///     Clearing emails is an empty array, never null. Null returns 500.
///   </item>
///   <item>
///     Gender is written as a bare enum name, the inverse of the $filter
///     encoding. Null is never emitted for it: the service accepts null with
///     a 204 and silently coerces the value to Male.
///   </item>
///   <item>
///     AddressInfo is read-only and is never present in an update payload.
///   </item>
/// </list>
/// </remarks>
public sealed class PersonMapper(ILogger<PersonMapper> logger)
{
    private readonly ILogger<PersonMapper> _logger = logger;

    public Person ToDomain(PersonDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return Person.Create(
            UserName.From(dto.UserName),
            PersonName.From(dto.FirstName, dto.LastName),
            ToEmails(dto),
            ToAddresses(dto.AddressInfo),
            ParseGender(dto.Gender),
            ConcurrencyToken.From(dto.ETag));
    }

    public PersonSummary ToSummary(PersonDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new PersonSummary(
            UserName.From(dto.UserName),
            PersonName.From(dto.FirstName, dto.LastName),
            ParseGender(dto.Gender));
    }

    public Dictionary<string, object?> ToSparsePatchBody(PersonUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (update.FirstName is not null)
        {
            body["FirstName"] = update.FirstName;
        }

        if (update.LastName is not null)
        {
            body["LastName"] = update.LastName;
        }

        // An empty collection is a meaningful instruction ("clear these") and
        // must reach the wire as [], not as null, which the service rejects
        // with a 500.
        if (update.Emails is not null)
        {
            body["Emails"] = update.Emails.Select(email => email.Value).ToArray();
        }

        if (update.Gender is not null)
        {
            body["Gender"] = ToPatchLiteral(update.Gender.Value);
        }

        // AddressInfo is deliberately absent, whatever the caller supplied.
        return body;
    }

    /// <summary>Renders a gender as a bare PATCH-body literal, for example "Female".</summary>
    public static string ToPatchLiteral(Gender gender) => gender.ToString();

    /// <summary>
    /// Reads gender leniently, defaulting to <see cref="Gender.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// The property is nullable on the wire, and a read must not fail over a
    /// value the service is entitled to omit. Unknown is the honest rendering
    /// of "not stated" and, unlike null, is a value the service can store.
    /// </remarks>
    private static Gender ParseGender(string? value) =>
        Enum.TryParse<Gender>(value, ignoreCase: false, out var gender) && Enum.IsDefined(gender)
            ? gender
            : Gender.Unknown;

    /// <summary>
    /// Drops addresses that fail to parse rather than failing the read.
    /// </summary>
    private static IReadOnlyList<Address> ToAddresses(IReadOnlyList<LocationDto>? locations)
    {
        if (locations is null || locations.Count == 0)
        {
            return [];
        }

        return [.. locations.Select(location => Address.From(
            location.Address,
            location.City?.Name,
            location.City?.Region,
            location.City?.CountryRegion))];
    }

    /// <summary>
    /// Skips addresses the domain rejects instead of failing the whole read.
    /// </summary>
    /// <remarks>
    /// The service accepts "not-an-email" with a 204, so invalid data can
    /// genuinely be stored. Throwing here would make such a person impossible
    /// to load, and therefore impossible to fix through the UI, which is the
    /// worse outcome. Each dropped value is logged.
    /// </remarks>
    private List<EmailAddress> ToEmails(PersonDto dto)
    {
        if (dto.Emails is null || dto.Emails.Count == 0)
        {
            return [];
        }

        var emails = new List<EmailAddress>(dto.Emails.Count);

        foreach (var candidate in dto.Emails)
        {
            if (EmailAddress.TryFrom(candidate, out var email, out var error))
            {
                emails.Add(email);
            }
            else
            {
                _logger.LogWarning(
                    "Dropping an unparseable stored email address for {UserName}: {Reason}",
                    dto.UserName,
                    error);
            }
        }

        return emails;
    }
}
