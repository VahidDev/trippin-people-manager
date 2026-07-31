using TripPin.Application.People.Models;
using TripPin.Domain.People;

namespace TripPin.Infrastructure.OData;

/// <summary>
/// Translates a <see cref="PersonFilter"/> into an OData $filter expression.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PersonMapper"/> because the read and write
/// encodings of an enum are inverted, and a shared helper would be wrong on
/// one side by construction:
/// <list type="bullet">
///   <item>
///     In $filter the enum must be fully qualified.
///     <c>Gender eq Microsoft.OData.SampleService.Models.TripPin.PersonGender'Female'</c>
///     succeeds; the bare <c>'Female'</c> returns 500.
///   </item>
///   <item>
///     In a PATCH body it is exactly the opposite: bare <c>"Female"</c>
///     succeeds and the qualified form returns 500. See <see cref="PersonMapper"/>.
///   </item>
/// </list>
/// <para>
/// This and <see cref="ODataQueryBuilder"/> are the only two places a filter
/// exists as a string. Everything above them passes the structured
/// <see cref="PersonFilter"/>.
/// </para>
/// </remarks>
public sealed class ODataFilterTranslator
{
    /// <summary>Namespace-qualified enum type name, required in $filter only.</summary>
    public const string GenderEnumTypeName =
        "Microsoft.OData.SampleService.Models.TripPin.PersonGender";

    /// <summary>Returns an empty string for an empty filter, meaning "match everything".</summary>
    public string Translate(PersonFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var clauses = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(filter.NameContains))
        {
            var literal = ODataQueryBuilder.EscapeLiteral(filter.NameContains.Trim());

            clauses.Add(
                $"(contains(FirstName,{literal})" +
                $" or contains(LastName,{literal})" +
                $" or contains(UserName,{literal}))");
        }

        if (filter.Gender is not null)
        {
            clauses.Add($"Gender eq {ToFilterLiteral(filter.Gender.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.EmailContains))
        {
            var literal = ODataQueryBuilder.EscapeLiteral(filter.EmailContains.Trim());

            clauses.Add($"Emails/any(e: contains(e,{literal}))");
        }

        return string.Join(" and ", clauses);
    }

    /// <summary>Renders a gender as a fully-qualified $filter enum literal.</summary>
    public static string ToFilterLiteral(Gender gender) => $"{GenderEnumTypeName}'{gender}'";
}
