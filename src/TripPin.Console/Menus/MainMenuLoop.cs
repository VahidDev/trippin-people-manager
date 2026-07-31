using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripPin.Console.Input;
using TripPin.Console.Rendering;
using TripPin.Domain.People;
using TripPin.Infrastructure.Configuration;
using SystemConsole = System.Console;

namespace TripPin.Console.Menus;

/// <summary>
/// Top-level dispatch loop. Owns no business rules: reads a choice, delegates
/// to a screen, renders the outcome.
/// </summary>
public sealed class MainMenuLoop(
    PeopleListScreen listScreen,
    PersonDetailScreen detailScreen,
    PersonEditScreen editScreen,
    ConsoleReader reader,
    ResultPresenter presenter,
    IHostApplicationLifetime lifetime,
    IOptions<TripPinOptions> options,
    ILogger<MainMenuLoop> logger)
{
    private readonly PeopleListScreen _listScreen = listScreen;
    private readonly PersonDetailScreen _detailScreen = detailScreen;
    private readonly PersonEditScreen _editScreen = editScreen;
    private readonly ConsoleReader _reader = reader;
    private readonly ResultPresenter _presenter = presenter;
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly TripPinOptions _options = options.Value;
    private readonly ILogger<MainMenuLoop> _logger = logger;

    /// <summary>
    /// A backstop above the per-request HttpClient timeout, generous enough for
    /// the multi-request operations (a list read is a count plus a page).
    /// </summary>
    private TimeSpan OperationTimeout =>
        TimeSpan.FromSeconds(_options.RequestTimeoutSeconds * 3);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = new OperationScope(cancellationToken);

        SystemConsole.WriteLine();
        SystemConsole.WriteLine("=================================");
        SystemConsole.WriteLine(" TripPin People Manager");
        SystemConsole.WriteLine("=================================");
        SystemConsole.WriteLine($" Service: {_options.BaseAddress}");

        while (!cancellationToken.IsCancellationRequested)
        {
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("Main menu");
            SystemConsole.WriteLine("  1  List people");
            SystemConsole.WriteLine("  2  Search people");
            SystemConsole.WriteLine("  3  View a person");
            SystemConsole.WriteLine("  4  Edit a person");
            SystemConsole.WriteLine("  q  Quit");

            var choice = await _reader.PromptAsync("> ", cancellationToken).ConfigureAwait(false);

            // Null is Ctrl+C or end of input. Both mean stop.
            if (choice is null || string.Equals(choice.Trim(), "q", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            try
            {
                await DispatchAsync(choice.Trim(), scope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SystemConsole.WriteLine();
                SystemConsole.WriteLine("  Shutting down.");
                break;
            }
            catch (OperationCanceledException)
            {
                // The linked token fired on its own, so this was the timeout
                // rather than the user.
                SystemConsole.WriteLine();
                SystemConsole.WriteLine("  That took too long and was cancelled. Please try again.");
            }
            catch (Exception exception)
            {
                // Full detail to the log, a plain sentence to the screen. A
                // stack trace helps nobody sitting at a menu.
                _logger.LogError(exception, "Unhandled failure while running menu option {Choice}.", choice);

                SystemConsole.WriteLine();
                SystemConsole.WriteLine($"  {ResultPresenter.UnexpectedFailure}");
            }
        }

        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Goodbye.");

        _lifetime.StopApplication();
    }

    private async Task DispatchAsync(
        string choice,
        OperationScope scope,
        CancellationToken cancellationToken)
    {
        switch (choice)
        {
            case "1":
                await _listScreen.ShowAsync(scope, cancellationToken).ConfigureAwait(false);
                await OpenRequestedAsync(scope, cancellationToken).ConfigureAwait(false);
                break;

            case "2":
                await _listScreen.SearchAsync(scope, cancellationToken).ConfigureAwait(false);
                await OpenRequestedAsync(scope, cancellationToken).ConfigureAwait(false);
                break;

            case "3":
                await ViewAsync(scope, cancellationToken).ConfigureAwait(false);
                break;

            case "4":
                await EditAsync(scope, cancellationToken).ConfigureAwait(false);
                break;

            default:
                SystemConsole.WriteLine("  Not an option. Choose 1, 2, 3, 4 or q.");
                break;
        }
    }

    /// <summary>Follows through when a browse screen was asked to open someone.</summary>
    private async Task OpenRequestedAsync(OperationScope scope, CancellationToken cancellationToken)
    {
        if (TryParse(_listScreen.RequestedUserName, out var userName))
        {
            await _detailScreen
                .ShowAsync(userName, scope.Begin(OperationTimeout), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ViewAsync(OperationScope scope, CancellationToken cancellationToken)
    {
        var entered = await _reader.PromptAsync("  User name: ", cancellationToken)
            .ConfigureAwait(false);

        if (!TryParse(entered, out var userName))
        {
            SystemConsole.WriteLine("  A user name is required.");
            return;
        }

        await _detailScreen
            .ShowAsync(userName, scope.Begin(OperationTimeout), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EditAsync(OperationScope scope, CancellationToken cancellationToken)
    {
        var entered = await _reader.PromptAsync("  User name: ", cancellationToken)
            .ConfigureAwait(false);

        if (!TryParse(entered, out var userName))
        {
            SystemConsole.WriteLine("  A user name is required.");
            return;
        }

        await _editScreen
            .EditAsync(userName, scope, OperationTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Uses the domain's own parser, so the key rule lives in one place rather
    /// than being restated here.
    /// </summary>
    private bool TryParse(string? entered, [NotNullWhen(true)] out UserName? userName)
    {
        if (UserName.TryFrom(entered, out var parsed, out var error))
        {
            userName = parsed;
            return true;
        }

        _logger.LogDebug("Rejected user name input: {Reason}", error);

        userName = null;
        return false;
    }
}
