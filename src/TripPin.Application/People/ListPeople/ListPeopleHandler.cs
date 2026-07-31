using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Application.People.Ports;

namespace TripPin.Application.People.ListPeople;

public sealed class ListPeopleHandler(IPeopleRepository repository)
    : IQueryHandler<ListPeopleQuery, PagedResult<PersonSummary>>
{
    private readonly IPeopleRepository _repository = repository;

    public async Task<Result<PagedResult<PersonSummary>>> HandleAsync(
        ListPeopleQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var errors = Paging.Validate(query.Page, query.PageSize);

        if (errors.Count > 0)
        {
            return Result<PagedResult<PersonSummary>>.ValidationFailed(errors);
        }

        return await _repository
            .ListAsync(query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);
    }
}
