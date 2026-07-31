using System.Diagnostics.CodeAnalysis;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Ports;
using TripPin.Application.People.UpdatePerson;
using TripPin.Console.Input;
using TripPin.Console.Rendering;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using SystemConsole = System.Console;

namespace TripPin.Console.Menus;

/// <summary>
/// Edit form for a person's writable fields.
/// </summary>
/// <remarks>
/// Prompts for first name, last name, emails and gender. UserName is shown but
/// never offered for editing: the service accepts a change to it, returns 204
/// and ignores it. Addresses are displayed read-only.
/// <para>
/// A blank answer means "leave unchanged", which is what produces the sparse
/// update. Clearing the email list is a distinct, explicit action, because an
/// empty list and an untouched list mean different things on the wire.
/// </para>
/// <para>
/// <see cref="IPeopleRepository"/> is injected alongside the command handler
/// solely for the fresh pre-edit read. It is an Application-owned port, so this
/// stays within the dependency rules, and no use case covers a read whose only
/// purpose is to obtain a current token.
/// </para>
/// </remarks>
public sealed class PersonEditScreen(
    ICommandHandler<UpdatePersonCommand, ConcurrencyToken> handler,
    IPeopleRepository repository,
    PersonRenderer renderer,
    ResultPresenter presenter,
    ConsoleReader reader)
{
    private readonly ICommandHandler<UpdatePersonCommand, ConcurrencyToken> _handler = handler;
    private readonly IPeopleRepository _repository = repository;
    private readonly PersonRenderer _renderer = renderer;
    private readonly ResultPresenter _presenter = presenter;
    private readonly ConsoleReader _reader = reader;

    public async Task EditAsync(
        UserName userName,
        OperationScope scope,
        TimeSpan operationTimeout,
        CancellationToken inputToken)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(scope);

        while (!inputToken.IsCancellationRequested)
        {
            // Always the uncached read. A cached entity carries a cached ETag,
            // and a stale ETag is a rejected write.
            var loaded = await _repository
                .GetForUpdateAsync(userName, scope.Begin(operationTimeout))
                .ConfigureAwait(false);

            if (!loaded.IsSuccess)
            {
                SystemConsole.WriteLine();
                SystemConsole.WriteLine($"  {_presenter.Describe(loaded)}");
                return;
            }

            var person = loaded.Value;

            SystemConsole.WriteLine();
            SystemConsole.WriteLine($"--- Edit {person.UserName.Value} ---");
            SystemConsole.WriteLine(_renderer.RenderDetail(person));
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("  Leave a field blank to leave it unchanged.");

            var command = await CollectAsync(person, inputToken).ConfigureAwait(false);
            var changes = Summarize(person, command);

            if (changes.Count == 0)
            {
                SystemConsole.WriteLine();
                SystemConsole.WriteLine("  Nothing was changed, so nothing was saved.");
                return;
            }

            SystemConsole.WriteLine();
            SystemConsole.WriteLine("  --- Changes to save ---");

            foreach (var change in changes)
            {
                SystemConsole.WriteLine($"    {change}");
            }

            SystemConsole.WriteLine();

            if (!await _reader.ConfirmAsync("  Save these changes?", inputToken).ConfigureAwait(false))
            {
                SystemConsole.WriteLine("  Nothing was saved.");
                return;
            }

            var result = await _handler
                .HandleAsync(command, scope.Begin(operationTimeout))
                .ConfigureAwait(false);

            SystemConsole.WriteLine();

            if (result.IsSuccess)
            {
                SystemConsole.WriteLine($"  Saved. New version: {result.Value.Value}");
                return;
            }

            // A conflict is the expected outcome of two people editing at once.
            // Never retried silently: the record on the server is not what this
            // user was looking at, so they have to see the new state first.
            if (result.Status == ResultStatus.ConcurrencyConflict)
            {
                SystemConsole.WriteLine($"  {_presenter.Describe(result)}");
                SystemConsole.WriteLine("  Someone else changed this person while you were editing.");

                if (await _reader
                    .ConfirmAsync("  Reload the current values and try again?", inputToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                SystemConsole.WriteLine("  Nothing was saved.");
                return;
            }

            SystemConsole.WriteLine($"  {_presenter.Describe(result)}");
            return;
        }
    }

    /// <summary>
    /// Builds the command. Untouched fields stay null so they are never sent.
    /// </summary>
    private async Task<UpdatePersonCommand> CollectAsync(Person person, CancellationToken inputToken)
    {
        var firstName = await _reader
            .PromptOptionalAsync("  First name", person.Name.First, inputToken)
            .ConfigureAwait(false);

        var lastName = await _reader
            .PromptOptionalAsync("  Last name ", person.Name.Last, inputToken)
            .ConfigureAwait(false);

        var gender = await PromptGenderAsync(person, inputToken).ConfigureAwait(false);
        var emails = await PromptEmailsAsync(inputToken).ConfigureAwait(false);

        return new UpdatePersonCommand
        {
            UserName = person.UserName,

            // The token from the read above, not a re-fetched one. This is what
            // makes a conflict mean "somebody else changed it".
            Concurrency = person.Concurrency,

            // Re-typing the existing value counts as no change, so the field
            // stays out of the payload and the confirmation summary is exactly
            // what gets sent.
            FirstName = ChangedOrNull(firstName, person.Name.First),
            LastName = ChangedOrNull(lastName, person.Name.Last),
            Gender = gender == person.Gender ? null : gender,

            // Not suppressed on equality: choosing [r] or [c] is an explicit
            // instruction, and honouring it as given is less surprising than
            // second-guessing it.
            Emails = emails,
        };
    }

    private static string? ChangedOrNull(string? entered, string current) =>
        Blank(entered) || string.Equals(entered.Trim(), current, StringComparison.Ordinal)
            ? null
            : entered.Trim();

    private async Task<Gender?> PromptGenderAsync(Person person, CancellationToken inputToken)
    {
        var answer = await _reader
            .PromptOptionalAsync(
                "  Gender (male/female/unknown)",
                person.Gender.ToString(),
                inputToken)
            .ConfigureAwait(false);

        if (Blank(answer))
        {
            return null;
        }

        if (Enum.TryParse<Gender>(answer.Trim(), ignoreCase: true, out var gender)
            && Enum.IsDefined(gender))
        {
            return gender;
        }

        SystemConsole.WriteLine("    Unrecognised gender; leaving it unchanged.");
        return null;
    }

    /// <summary>
    /// Three explicit outcomes, because the wire distinguishes them.
    /// </summary>
    /// <remarks>
    /// Keeping and clearing cannot share a prompt: null leaves the collection
    /// alone while an empty list wipes it, and the service rejects an explicit
    /// null with a 500. A single "edit emails" prompt where blank meant one or
    /// the other would make the destructive option the easiest to hit by
    /// accident.
    /// </remarks>
    private async Task<IReadOnlyList<string>?> PromptEmailsAsync(CancellationToken inputToken)
    {
        SystemConsole.WriteLine("  Emails:");
        SystemConsole.WriteLine("    [k] keep as they are    [r] replace with a new list    [c] clear all");

        var choice = await _reader.PromptAsync("    > ", inputToken).ConfigureAwait(false);

        switch (choice?.Trim().ToLowerInvariant())
        {
            case "c":
                return [];

            case "r":
                var entered = await _reader
                    .PromptAsync("    New emails (comma separated): ", inputToken)
                    .ConfigureAwait(false);

                if (Blank(entered))
                {
                    // Refused rather than guessed: an empty answer here could
                    // mean either intent, and one of them destroys data.
                    SystemConsole.WriteLine(
                        "    Nothing entered. Use [c] to clear them. Leaving them unchanged.");

                    return null;
                }

                return entered!
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            default:
                return null;
        }
    }

    /// <summary>Describes only the fields that will actually be sent.</summary>
    private static List<string> Summarize(Person person, UpdatePersonCommand command)
    {
        var changes = new List<string>(4);

        if (command.FirstName is not null)
        {
            changes.Add($"First name : {person.Name.First} -> {command.FirstName}");
        }

        if (command.LastName is not null)
        {
            changes.Add($"Last name  : {person.Name.Last} -> {command.LastName}");
        }

        if (command.Gender is not null)
        {
            changes.Add($"Gender     : {person.Gender} -> {command.Gender}");
        }

        if (command.Emails is not null)
        {
            changes.Add(command.Emails.Count == 0
                ? "Emails     : cleared (all removed)"
                : $"Emails     : replaced with {string.Join(", ", command.Emails)}");
        }

        return changes;
    }

    /// <summary>
    /// Annotated so the compiler knows a false result means non-null, which
    /// keeps the call sites free of the null-forgiving operator.
    /// </summary>
    private static bool Blank([NotNullWhen(false)] string? value) =>
        string.IsNullOrWhiteSpace(value);
}
