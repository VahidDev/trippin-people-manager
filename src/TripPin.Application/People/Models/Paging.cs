namespace TripPin.Application.People.Models;

/// <summary>
/// Bounds on paging arguments, applied by the query handlers.
/// </summary>
/// <remarks>
/// The upper bound is a client-side guard, not a service limit. The service
/// pages at 8 rows whatever we ask for, so an unbounded page size would
/// silently become a long chain of round trips rather than one big response.
/// </remarks>
public static class Paging
{
    public const int MinPage = 1;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public static IReadOnlyList<string> Validate(int page, int pageSize)
    {
        var errors = new List<string>();

        if (page < MinPage)
        {
            errors.Add($"Page must be {MinPage} or greater.");
        }

        if (pageSize is < MinPageSize or > MaxPageSize)
        {
            errors.Add($"Page size must be between {MinPageSize} and {MaxPageSize}.");
        }

        return errors;
    }
}
