using FluentAssertions;
using TripPin.Application.Abstractions;
using TripPin.Application.People.GetPersonDetails;
using TripPin.Application.People.ListPeople;
using TripPin.Application.People.Models;
using TripPin.Application.People.SearchPeople;
using TripPin.Application.Tests.Fakes;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Application.Tests.People;

public sealed class ListPeopleHandlerTests
{
    private readonly FakePeopleRepository _repository = new();

    private ListPeopleHandler Handler => new(_repository);

    [Fact]
    public async Task Valid_paging_reaches_the_repository()
    {
        var result = await Handler.HandleAsync(
            new ListPeopleQuery(2, 8),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _repository.ListCalls.Should().Be(1);
        _repository.LastPage.Should().Be(2);
        _repository.LastPageSize.Should().Be(8);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(-1, 8)]
    [InlineData(1, 0)]
    [InlineData(1, Paging.MaxPageSize + 1)]
    public async Task Invalid_paging_is_rejected_before_any_call(int page, int pageSize)
    {
        var result = await Handler.HandleAsync(
            new ListPeopleQuery(page, pageSize),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        _repository.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task Both_paging_errors_are_reported_together()
    {
        var result = await Handler.HandleAsync(
            new ListPeopleQuery(0, 0),
            TestContext.Current.CancellationToken);

        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_cancellation_token_is_passed_through()
    {
        using var cts = new CancellationTokenSource();

        await Handler.HandleAsync(new ListPeopleQuery(1, 8), cts.Token);

        _repository.LastCancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task A_transport_failure_is_propagated_rather_than_thrown()
    {
        _repository.ListResult = Result<PagedResult<PersonSummary>>.Failure(
            ResultStatus.TransportFailure, "circuit open");

        var result = await Handler.HandleAsync(
            new ListPeopleQuery(1, 8),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.TransportFailure);
    }

    [Fact]
    public async Task A_null_query_is_a_programming_error()
    {
        var act = async () =>
            await Handler.HandleAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

public sealed class SearchPeopleHandlerTests
{
    private readonly FakePeopleRepository _repository = new();

    private SearchPeopleHandler Handler => new(_repository);

    [Fact]
    public async Task The_filter_is_passed_through_untouched()
    {
        var filter = new PersonFilter { NameContains = "russ", Gender = Gender.Male };

        var result = await Handler.HandleAsync(
            new SearchPeopleQuery(filter, 1, 8),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _repository.SearchCalls.Should().Be(1);
        _repository.LastFilter.Should().BeSameAs(filter);
    }

    [Fact]
    public async Task An_empty_filter_is_valid_and_matches_everything()
    {
        var result = await Handler.HandleAsync(
            new SearchPeopleQuery(PersonFilter.Empty, 1, 8),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _repository.SearchCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_missing_filter_is_rejected_before_any_call()
    {
        var result = await Handler.HandleAsync(
            new SearchPeopleQuery(null!, 1, 8),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().Contain("A search filter is required.");
        _repository.SearchCalls.Should().Be(0);
    }

    [Fact]
    public async Task Paging_errors_are_reported_alongside_a_missing_filter()
    {
        var result = await Handler.HandleAsync(
            new SearchPeopleQuery(null!, 0, 8),
            TestContext.Current.CancellationToken);

        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public async Task Invalid_paging_is_rejected_before_any_call()
    {
        var result = await Handler.HandleAsync(
            new SearchPeopleQuery(PersonFilter.Empty, 0, 8),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        _repository.SearchCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_cancellation_token_is_passed_through()
    {
        using var cts = new CancellationTokenSource();

        await Handler.HandleAsync(new SearchPeopleQuery(PersonFilter.Empty, 1, 8), cts.Token);

        _repository.LastCancellationToken.Should().Be(cts.Token);
    }
}

public sealed class GetPersonDetailsHandlerTests
{
    private readonly FakePeopleRepository _repository = new();

    private GetPersonDetailsHandler Handler => new(_repository);

    private static Person Russell() => Person.Create(
        UserName.From("russellwhyte"),
        PersonName.From("Russell", "Whyte"),
        [],
        [],
        Gender.Male,
        ConcurrencyToken.From("W/\"1\""));

    /// <summary>
    /// Reads for display go through the cacheable path, not the always-fresh
    /// one. Using GetForUpdateAsync here would defeat caching entirely.
    /// </summary>
    [Fact]
    public async Task A_display_read_uses_the_cacheable_path()
    {
        _repository.GetByIdResult = Result<Person>.Success(Russell());

        var result = await Handler.HandleAsync(
            new GetPersonDetailsQuery(UserName.From("russellwhyte")),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _repository.GetByIdCalls.Should().Be(1);
        _repository.GetForUpdateCalls.Should().Be(0);
    }

    /// <summary>
    /// The service returns 204 rather than 404 for a person that does not
    /// exist. By the time it reaches here that is a NotFound value, and no
    /// status code has leaked out of Infrastructure.
    /// </summary>
    [Fact]
    public async Task A_missing_person_is_a_NotFound_result_not_an_exception()
    {
        _repository.GetByIdResult = Result<Person>.NotFound("Person not found.");

        var result = await Handler.HandleAsync(
            new GetPersonDetailsQuery(UserName.From("nobody")),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_user_name_is_rejected_before_any_call()
    {
        var result = await Handler.HandleAsync(
            new GetPersonDetailsQuery(null!),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        _repository.GetByIdCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_cancellation_token_is_passed_through()
    {
        using var cts = new CancellationTokenSource();

        await Handler.HandleAsync(
            new GetPersonDetailsQuery(UserName.From("russellwhyte")),
            cts.Token);

        _repository.LastCancellationToken.Should().Be(cts.Token);
    }
}
