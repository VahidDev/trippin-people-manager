using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using TripPin.Application.Abstractions;
using TripPin.Application.People.ListPeople;
using TripPin.Application.People.Models;
using TripPin.Application.People.SearchPeople;
using TripPin.Console.Input;
using TripPin.Console.Rendering;
using TripPin.Domain.People;
using TripPin.Infrastructure.Configuration;
using SystemConsole = System.Console;

namespace TripPin.Console.Menus;

/// <summary>
/// Browsing and searching. Paging is presentational: the repository reports a
/// total count, so this screen can show "page 2 of 3" without knowing that the
/// service pages at 8 rows behind the scenes.
/// </summary>
public sealed class PeopleListScreen(
    IQueryHandler<ListPeopleQuery, PagedResult<PersonSummary>> listHandler,
    IQueryHandler<SearchPeopleQuery, PagedResult<PersonSummary>> searchHandler,
    PersonRenderer renderer,
    ResultPresenter presenter,
    ConsoleReader reader,
    IOptions<TripPinOptions> options)
{
    private readonly IQueryHandler<ListPeopleQuery, PagedResult<PersonSummary>> _listHandler = listHandler;
    private readonly IQueryHandler<SearchPeopleQuery, PagedResult<PersonSummary>> _searchHandler = searchHandler;
    private readonly PersonRenderer _renderer = renderer;
    private readonly ResultPresenter _presenter = presenter;
    private readonly ConsoleReader _reader = reader;
    private readonly TripPinOptions _options = options.Value;

    public string? RequestedUserName { get; private set; }

    public Task ShowAsync(OperationScope scope, CancellationToken cancellationToken) =>
        BrowseAsync(
            "All people",
            (page, token) => _listHandler.HandleAsync(
                new ListPeopleQuery(page, _options.PageSize), token),
            scope,
            cancellationToken);

    public async Task SearchAsync(OperationScope scope, CancellationToken cancellationToken)
    {
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("--- Search people ---");
        SystemConsole.WriteLine("Leave a field blank to ignore it.");

        var name = await _reader.PromptAsync("  Name contains  : ", cancellationToken)
            .ConfigureAwait(false);
        var email = await _reader.PromptAsync("  Email contains : ", cancellationToken)
            .ConfigureAwait(false);
        var gender = await _reader
            .PromptAsync("  Gender (male/female/unknown) : ", cancellationToken)
            .ConfigureAwait(false);

        // Every one of these three is a filter the service actually supports.
        // AddressInfo is filterable too, but it is read-only here, so it is not
        // offered.
        var filter = new PersonFilter
        {
            NameContains = Blank(name) ? null : name.Trim(),
            EmailContains = Blank(email) ? null : email.Trim(),
            Gender = ParseGender(gender),
        };

        if (!Blank(gender) && filter.Gender is null)
        {
            SystemConsole.WriteLine("  Unrecognised gender; ignoring that field.");
        }

        await BrowseAsync(
            "Search results",
            (page, token) => _searchHandler.HandleAsync(
                new SearchPeopleQuery(filter, page, _options.PageSize), token),
            scope,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared paging loop. A fresh token is taken per page, so each fetch gets
    /// its own timeout and supersedes the previous one.
    /// </summary>
    private async Task BrowseAsync(
        string title,
        Func<int, CancellationToken, Task<Result<PagedResult<PersonSummary>>>> fetch,
        OperationScope scope,
        CancellationToken cancellationToken)
    {
        RequestedUserName = null;
        var page = 1;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await fetch(page, scope.Begin(OperationTimeout)).ConfigureAwait(false);

            SystemConsole.WriteLine();
            SystemConsole.WriteLine($"--- {title} ---");

            if (!result.IsSuccess)
            {
                SystemConsole.WriteLine($"  {_presenter.Describe(result)}");
                return;
            }

            var body = result.Value;

            SystemConsole.WriteLine(_renderer.RenderTable(body));
            SystemConsole.WriteLine();
            SystemConsole.WriteLine(_renderer.RenderPageFooter(body));

            var canGoForward = body.HasMore;
            var canGoBack = page > 1;

            SystemConsole.WriteLine(Options(canGoForward, canGoBack));

            var choice = await _reader.PromptAsync("> ", cancellationToken).ConfigureAwait(false);

            // Null means Ctrl+C or end of input; either way, leave the screen.
            switch (choice?.Trim().ToLowerInvariant())
            {
                case "n" when canGoForward:
                    page++;
                    break;

                case "p" when canGoBack:
                    page--;
                    break;

                case "v":
                    RequestedUserName = await _reader
                        .PromptAsync("  User name: ", cancellationToken)
                        .ConfigureAwait(false);
                    return;

                case null:
                case "b":
                    return;

                default:
                    SystemConsole.WriteLine("  Not an option here.");
                    break;
            }
        }
    }

    private TimeSpan OperationTimeout =>
        TimeSpan.FromSeconds(_options.RequestTimeoutSeconds * 3);

    private static string Options(bool canGoForward, bool canGoBack)
    {
        var choices = new List<string>(4);

        if (canGoForward)
        {
            choices.Add("[n]ext");
        }

        if (canGoBack)
        {
            choices.Add("[p]revious");
        }

        choices.Add("[v]iew a person");
        choices.Add("[b]ack");

        return $"  {string.Join("  ", choices)}";
    }

    /// <summary>
    /// Annotated so the compiler knows a false result means non-null, which
    /// keeps the call sites free of the null-forgiving operator.
    /// </summary>
    private static bool Blank([NotNullWhen(false)] string? value) =>
        string.IsNullOrWhiteSpace(value);

    private static Gender? ParseGender(string? value) =>
        Blank(value)
            ? null
            : Enum.TryParse<Gender>(value.Trim(), ignoreCase: true, out var gender)
                && Enum.IsDefined(gender)
                    ? gender
                    : null;
}
