using TripPin.Application.Abstractions;
using TripPin.Application.People.GetPersonDetails;
using TripPin.Console.Input;
using TripPin.Console.Rendering;
using TripPin.Domain.People;
using SystemConsole = System.Console;

namespace TripPin.Console.Menus;

/// <summary>
/// Single-person view. A missing person arrives as
/// <see cref="ResultStatus.NotFound"/> and is reported as such; the
/// 204-instead-of-404 quirk behind it never reaches this layer.
/// </summary>
public sealed class PersonDetailScreen(
    IQueryHandler<GetPersonDetailsQuery, Person> handler,
    PersonRenderer renderer,
    ResultPresenter presenter,
    ConsoleReader reader)
{
    private readonly IQueryHandler<GetPersonDetailsQuery, Person> _handler = handler;
    private readonly PersonRenderer _renderer = renderer;
    private readonly ResultPresenter _presenter = presenter;
    private readonly ConsoleReader _reader = reader;

    /// <summary>
    /// Reads through the cacheable path, which is correct for a display-only
    /// view. The edit screen deliberately does not.
    /// </summary>
    public async Task ShowAsync(
        UserName userName,
        CancellationToken operationToken,
        CancellationToken inputToken)
    {
        ArgumentNullException.ThrowIfNull(userName);

        var result = await _handler
            .HandleAsync(new GetPersonDetailsQuery(userName), operationToken)
            .ConfigureAwait(false);

        SystemConsole.WriteLine();

        if (!result.IsSuccess)
        {
            SystemConsole.WriteLine($"  {_presenter.Describe(result)}");
            return;
        }

        SystemConsole.WriteLine($"--- {result.Value.Name} ---");
        SystemConsole.WriteLine(_renderer.RenderDetail(result.Value));
        SystemConsole.WriteLine();

        await _reader.PromptAsync("  (Enter to continue) ", inputToken).ConfigureAwait(false);
    }
}
