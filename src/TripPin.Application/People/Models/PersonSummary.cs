using TripPin.Domain.People;

namespace TripPin.Application.People.Models;

/// <summary>
/// Slim projection for list and search screens, matching what the repository
/// requests via $select rather than pulling whole entities.
/// </summary>
public sealed record PersonSummary(
    UserName UserName,
    PersonName Name,
    Gender Gender);
