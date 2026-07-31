using TripPin.Domain.People;

namespace TripPin.Application.People.GetPersonDetails;

/// <summary>Fetch one person. Resolves to NotFound when the service returns 204.</summary>
public sealed record GetPersonDetailsQuery(UserName UserName);
