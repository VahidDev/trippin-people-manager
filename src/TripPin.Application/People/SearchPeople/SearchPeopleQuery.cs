using TripPin.Application.People.Models;

namespace TripPin.Application.People.SearchPeople;

/// <summary>Filtered browse. The filter is structured, never a raw OData string.</summary>
public sealed record SearchPeopleQuery(PersonFilter Filter, int Page, int PageSize);
