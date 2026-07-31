namespace TripPin.Application.Abstractions;

/// <summary>The write-side counterpart to <see cref="IQueryHandler{TQuery,TResult}"/>.</summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
