namespace TripPin.Application.People.Models;

/// <summary>
/// One page of results, plus the total the service reported.
/// </summary>
/// <remarks>
/// Hides server-driven paging. The service returns 8 rows per page out of 20
/// and supplies an @odata.nextLink, but the console wants "page 2 of 3", so
/// the repository drives $top/$skip and reports $count here. Callers never
/// see a skiptoken.
/// </remarks>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    public bool HasMore => (Page * PageSize) < TotalCount;
}
