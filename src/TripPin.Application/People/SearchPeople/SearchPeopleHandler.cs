using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Application.People.Ports;

namespace TripPin.Application.People.SearchPeople;

public sealed class SearchPeopleHandler(IPeopleRepository repository)
    : IQueryHandler<SearchPeopleQuery, PagedResult<PersonSummary>>
{
    private readonly IPeopleRepository _repository = repository;

    public async Task<Result<PagedResult<PersonSummary>>> HandleAsync(
        SearchPeopleQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var errors = new List<string>(Paging.Validate(query.Page, query.PageSize));

        // Returns early rather than falling through, so the compiler can see
        // that Filter is non-null below. Paging errors are still reported
        // alongside, because they were collected first.
        if (query.Filter is null)
        {
            errors.Add("A search filter is required.");
            return Result<PagedResult<PersonSummary>>.ValidationFailed(errors);
        }

        if (errors.Count > 0)
        {
            return Result<PagedResult<PersonSummary>>.ValidationFailed(errors);
        }

        return await _repository
            .SearchAsync(query.Filter, query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);
    }
}
