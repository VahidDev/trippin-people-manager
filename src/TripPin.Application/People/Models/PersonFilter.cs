using TripPin.Domain.People;

namespace TripPin.Application.People.Models;

/// <summary>
/// Structured search intent.
/// </summary>
/// <remarks>
/// Deliberately not a raw OData string. An unrecognised field name in $filter
/// does not error, it returns 200 with zero rows, so a typo is
/// indistinguishable from "no matches". Keeping the query structured turns
/// that class of mistake into a compile error. Translation to OData syntax
/// happens in Infrastructure and nowhere else.
/// </remarks>
public sealed record PersonFilter
{
    /// <summary>Matched against first name, last name and user name.</summary>
    public string? NameContains { get; init; }

    public Gender? Gender { get; init; }

    public string? EmailContains { get; init; }

    public static PersonFilter Empty { get; } = new();
}
