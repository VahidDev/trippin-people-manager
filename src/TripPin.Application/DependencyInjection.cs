using Microsoft.Extensions.DependencyInjection;
using TripPin.Application.Abstractions;
using TripPin.Application.People.GetPersonDetails;
using TripPin.Application.People.ListPeople;
using TripPin.Application.People.Models;
using TripPin.Application.People.SearchPeople;
using TripPin.Application.People.UpdatePerson;
using TripPin.Domain.Common;
using TripPin.Domain.People;

namespace TripPin.Application;

/// <summary>
/// Registers the use cases. Knows nothing about HTTP, caching or logging
/// sinks: those are wired by Infrastructure against the ports declared here.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<
            IQueryHandler<ListPeopleQuery, PagedResult<PersonSummary>>, ListPeopleHandler>();
        services.AddScoped<
            IQueryHandler<SearchPeopleQuery, PagedResult<PersonSummary>>, SearchPeopleHandler>();
        services.AddScoped<
            IQueryHandler<GetPersonDetailsQuery, Person>, GetPersonDetailsHandler>();
        services.AddScoped<
            ICommandHandler<UpdatePersonCommand, ConcurrencyToken>, UpdatePersonHandler>();

        return services;
    }
}
