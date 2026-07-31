using TripPin.Application.Abstractions;
using TripPin.Application.People.Ports;
using TripPin.Domain.Common;

namespace TripPin.Application.People.UpdatePerson;

/// <summary>
/// Validates the command, then writes it with the caller's own token.
/// </summary>
/// <remarks>
/// Deliberately does not re-read the person first. The token in the command
/// came from the caller's <see cref="IPeopleRepository.GetForUpdateAsync"/>
/// call, and re-reading here would replace it with a fresh one, so a
/// concurrent edit by someone else would be overwritten silently instead of
/// reported as a conflict.
/// </remarks>
public sealed class UpdatePersonHandler(IPeopleRepository repository)
    : ICommandHandler<UpdatePersonCommand, ConcurrencyToken>
{
    private readonly IPeopleRepository _repository = repository;

    public async Task<Result<ConcurrencyToken>> HandleAsync(
        UpdatePersonCommand command,
        CancellationToken cancellationToken)
    {
        var validated = UpdatePersonValidator.Validate(command);

        if (!validated.IsSuccess)
        {
            return Result<ConcurrencyToken>.Failure(validated.Status, validated.Errors);
        }

        return await _repository
            .UpdateAsync(validated.Value, cancellationToken)
            .ConfigureAwait(false);
    }
}
