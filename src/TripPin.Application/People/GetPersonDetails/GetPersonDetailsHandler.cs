using TripPin.Application.Abstractions;
using TripPin.Application.People.Ports;
using TripPin.Domain.People;

namespace TripPin.Application.People.GetPersonDetails;

public sealed class GetPersonDetailsHandler(IPeopleRepository repository)
    : IQueryHandler<GetPersonDetailsQuery, Person>
{
    private readonly IPeopleRepository _repository = repository;

    public async Task<Result<Person>> HandleAsync(
        GetPersonDetailsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.UserName is null)
        {
            return Result<Person>.ValidationFailed(["A user name is required."]);
        }

        return await _repository
            .GetByIdAsync(query.UserName, cancellationToken)
            .ConfigureAwait(false);
    }
}
