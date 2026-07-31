using FluentAssertions;
using TripPin.Application.Abstractions;
using TripPin.Application.People.UpdatePerson;
using TripPin.Application.Tests.Fakes;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Application.Tests.People;

public sealed class UpdatePersonHandlerTests
{
    private readonly FakePeopleRepository _repository = new();

    private UpdatePersonHandler Handler => new(_repository);

    private static UpdatePersonCommand Command(
        string? firstName = "Russ",
        IReadOnlyList<string>? emails = null,
        Gender? gender = null,
        string token = "W/\"caller-read\"") =>
        new()
        {
            UserName = UserName.From("russellwhyte"),
            Concurrency = ConcurrencyToken.From(token),
            FirstName = firstName,
            Emails = emails,
            Gender = gender,
        };

    [Fact]
    public async Task A_valid_command_reaches_the_repository_and_returns_the_new_token()
    {
        _repository.UpdateResult =
            Result<ConcurrencyToken>.Success(ConcurrencyToken.From("W/\"after\""));

        var result = await Handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("W/\"after\"");
        _repository.UpdateCalls.Should().Be(1);
    }

    /// <summary>
    /// The token must be the one the caller read, not one the handler fetched.
    /// Re-reading here would always produce a current token and would turn
    /// every concurrent edit into a silent overwrite.
    /// </summary>
    [Fact]
    public async Task The_caller_token_is_passed_through_untouched()
    {
        await Handler.HandleAsync(
            Command(token: "W/\"caller-read\""),
            TestContext.Current.CancellationToken);

        _repository.LastUpdate!.Concurrency.Value.Should().Be("W/\"caller-read\"");
    }

    [Fact]
    public async Task The_handler_does_not_re_read_the_person_first()
    {
        await Handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        _repository.GetForUpdateCalls.Should().Be(0);
        _repository.GetByIdCalls.Should().Be(0);
    }

    [Fact]
    public async Task Only_the_fields_the_command_set_are_sent()
    {
        await Handler.HandleAsync(
            Command(firstName: "Russ"),
            TestContext.Current.CancellationToken);

        var update = _repository.LastUpdate!;
        update.FirstName.Should().Be("Russ");
        update.LastName.Should().BeNull();
        update.Emails.Should().BeNull();
        update.Gender.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_emails_arrives_as_an_empty_list_not_null()
    {
        await Handler.HandleAsync(
            Command(firstName: null, emails: []),
            TestContext.Current.CancellationToken);

        _repository.LastUpdate!.Emails.Should().NotBeNull();
        _repository.LastUpdate.Emails.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_command_never_reaches_the_repository()
    {
        var result = await Handler.HandleAsync(
            Command(firstName: "   "),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().Equal("First name is required.");
        _repository.UpdateCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_command_that_changes_nothing_never_reaches_the_repository()
    {
        var result = await Handler.HandleAsync(
            Command(firstName: null),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        _repository.UpdateCalls.Should().Be(0);
    }

    /// <summary>
    /// A stale token comes back as a value, not an exception, so the console
    /// can offer to reload rather than showing a stack trace.
    /// </summary>
    [Fact]
    public async Task A_stale_token_is_reported_as_a_concurrency_conflict()
    {
        _repository.UpdateResult =
            Result<ConcurrencyToken>.Conflict("The record changed since it was read.");

        var result = await Handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.ConcurrencyConflict);
    }

    [Fact]
    public async Task A_transport_failure_is_propagated_rather_than_thrown()
    {
        _repository.UpdateResult =
            Result<ConcurrencyToken>.Failure(ResultStatus.TransportFailure, "socket closed");

        var result = await Handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.TransportFailure);
        result.Error.Should().Be("socket closed");
    }

    [Fact]
    public async Task The_cancellation_token_is_passed_through_to_the_repository()
    {
        using var cts = new CancellationTokenSource();

        await Handler.HandleAsync(Command(), cts.Token);

        _repository.LastCancellationToken.Should().Be(cts.Token);
    }
}
