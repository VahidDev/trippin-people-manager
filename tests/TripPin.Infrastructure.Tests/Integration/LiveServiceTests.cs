using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Application.People.Ports;
using TripPin.Domain.People;
using TripPin.Infrastructure.Session;
using Xunit;

namespace TripPin.Infrastructure.Tests.Integration;

/// <summary>
/// Tests that hit the real service. Excluded from the default run by the
/// Category trait, because they are slow, depend on a shared public sandbox
/// and mutate state.
/// </summary>
/// <remarks>
/// Run with <c>dotnet test --filter "Category=Integration"</c>.
/// <para>
/// Corruption is contained twice over. xUnit builds a fresh instance per test,
/// so each test resolves its own session and therefore its own pristine copy
/// of the data; and <see cref="DisposeAsync"/> calls ResetDataSource whether
/// the test passed or failed. A partial address write can poison the whole
/// People collection for the remainder of a session, so a failure must never
/// be able to strand the suite for the next run.
/// </para>
/// <para>
/// Wired through the real <c>AddInfrastructure</c> rather than by hand, so
/// these also serve as the check that the DI graph, handler order and session
/// resolution actually compose.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LiveServiceTests : IAsyncLifetime
{
    private ServiceProvider? _provider;
    private IServiceScope? _scope;
    private IPeopleRepository _repository = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TripPin:BaseAddress"] = "https://services.odata.org/v4/TripPinServiceRW/",
                ["TripPin:RequestTimeoutSeconds"] = "60",
                ["TripPin:PageSize"] = "8",

                // Off on purpose: these tests assert on what the service does,
                // and the decorator has its own unit tests.
                ["TripPin:Cache:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure(configuration);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _repository = _scope.ServiceProvider.GetRequiredService<IPeopleRepository>();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await ResetDataSourceAsync().ConfigureAwait(false);
        }

        _scope?.Dispose();

        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores the session's data. Deliberately swallows its own failures:
    /// teardown must not convert a passing test into a failing one, nor mask
    /// the real assertion failure of a failing one.
    /// </summary>
    private async Task ResetDataSourceAsync()
    {
        try
        {
            var session = _provider!.GetRequiredService<ISessionUriProvider>();
            var baseAddress = await session.GetBaseAddressAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var factory = _provider!.GetRequiredService<IHttpClientFactory>();
            using var client = factory.CreateClient();

            using var response = await client
                .PostAsync(new Uri(baseAddress, "ResetDataSource"), null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The session is discarded with this test either way.
        }
    }

    // -----------------------------------------------------------------
    // Session resolution
    // -----------------------------------------------------------------

    /// <summary>
    /// The one part of session handling a stubbed handler cannot cover, since
    /// redirect following belongs to the primary handler.
    /// </summary>
    [Fact]
    public async Task The_service_root_redirects_to_a_session_scoped_address()
    {
        var session = _provider!.GetRequiredService<ISessionUriProvider>();

        var resolved = await session.GetBaseAddressAsync(Token);

        resolved.AbsoluteUri.Should().Contain("(S(");
        resolved.AbsoluteUri.Should().EndWith("/TripPinServiceRW/");
    }

    [Fact]
    public async Task Every_request_in_a_run_uses_one_session()
    {
        var session = _provider!.GetRequiredService<ISessionUriProvider>();

        var first = await session.GetBaseAddressAsync(Token);
        await _repository.ListAsync(1, 8, Token);
        var second = await session.GetBaseAddressAsync(Token);

        second.Should().Be(first);
    }

    // -----------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListAsync_returns_a_page_and_the_total()
    {
        var result = await _repository.ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Items.Should().NotBeEmpty();
        result.Value.TotalCount.Should().Be(20, "the sample service seeds twenty people");
    }

    [Fact]
    public async Task Successive_pages_return_different_people()
    {
        var first = await _repository.ListAsync(1, 4, Token);
        var second = await _repository.ListAsync(2, 4, Token);

        first.IsSuccess.Should().BeTrue(first.Error);
        second.IsSuccess.Should().BeTrue(second.Error);

        var firstKeys = first.Value!.Items.Select(person => person.UserName.Value);
        var secondKeys = second.Value!.Items.Select(person => person.UserName.Value);

        firstKeys.Should().NotIntersectWith(secondKeys);
    }

    [Fact]
    public async Task GetByIdAsync_returns_a_known_person()
    {
        var result = await _repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Name.First.Should().Be("Russell");
        result.Value.Name.Last.Should().Be("Whyte");
        result.Value.Concurrency.Value.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The live proof of the 204-instead-of-404 mapping.
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_reports_a_missing_person_as_NotFound()
    {
        var result = await _repository.GetByIdAsync(
            UserName.From("nobodyhasthisname"),
            Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
    }

    [Fact]
    public async Task SearchAsync_matches_on_a_name_fragment()
    {
        var result = await _repository.SearchAsync(
            new PersonFilter { NameContains = "russ" },
            1,
            8,
            Token);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Items.Should().Contain(person => person.UserName.Value == "russellwhyte");
    }

    /// <summary>
    /// Proves the fully-qualified enum literal really is what $filter needs.
    /// The bare spelling returns 500 from this service.
    /// </summary>
    [Fact]
    public async Task SearchAsync_filters_on_the_gender_enum()
    {
        var result = await _repository.SearchAsync(
            new PersonFilter { Gender = Gender.Female },
            1,
            8,
            Token);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Items.Should().NotBeEmpty();
        result.Value.Items.Should().OnlyContain(person => person.Gender == Gender.Female);
    }

    [Fact]
    public async Task SearchAsync_returns_an_empty_page_for_a_fragment_nothing_matches()
    {
        var result = await _repository.SearchAsync(
            new PersonFilter { NameContains = "zzzznobody" },
            1,
            8,
            Token);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForUpdateAsync_returns_a_usable_token()
    {
        var result = await _repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Concurrency.Value.Should().StartWith("W/");
    }

    // -----------------------------------------------------------------
    // Writes
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_update_with_a_fresh_token_succeeds_and_persists()
    {
        var userName = UserName.From("russellwhyte");

        var loaded = await _repository.GetForUpdateAsync(userName, Token);
        loaded.IsSuccess.Should().BeTrue(loaded.Error);

        var updated = await _repository.UpdateAsync(
            new PersonUpdate
            {
                UserName = userName,
                Concurrency = loaded.Value!.Concurrency,
                FirstName = "Russell-Integration",
            },
            Token);

        updated.IsSuccess.Should().BeTrue(updated.Error);
        updated.Value!.Value.Should().NotBe(loaded.Value.Concurrency.Value);

        var reloaded = await _repository.GetByIdAsync(userName, Token);
        reloaded.Value!.Name.First.Should().Be("Russell-Integration");
    }

    [Fact]
    public async Task Clearing_emails_persists_as_an_empty_collection()
    {
        var userName = UserName.From("russellwhyte");

        var loaded = await _repository.GetForUpdateAsync(userName, Token);
        loaded.Value!.Emails.Should().NotBeEmpty("the seed data has emails to clear");

        var updated = await _repository.UpdateAsync(
            new PersonUpdate
            {
                UserName = userName,
                Concurrency = loaded.Value.Concurrency,
                Emails = [],
            },
            Token);

        updated.IsSuccess.Should().BeTrue(updated.Error);

        var reloaded = await _repository.GetByIdAsync(userName, Token);
        reloaded.Value!.Emails.Should().BeEmpty();
    }

    /// <summary>
    /// Optimistic concurrency proven end to end: the second write carries a
    /// token the first write has already superseded.
    /// </summary>
    [Fact]
    public async Task A_second_update_with_the_same_token_is_a_conflict()
    {
        var userName = UserName.From("russellwhyte");

        var loaded = await _repository.GetForUpdateAsync(userName, Token);
        var staleToken = loaded.Value!.Concurrency;

        var first = await _repository.UpdateAsync(
            new PersonUpdate
            {
                UserName = userName,
                Concurrency = staleToken,
                FirstName = "FirstWriter",
            },
            Token);

        first.IsSuccess.Should().BeTrue(first.Error);

        var second = await _repository.UpdateAsync(
            new PersonUpdate
            {
                UserName = userName,
                Concurrency = staleToken,
                FirstName = "SecondWriter",
            },
            Token);

        second.IsSuccess.Should().BeFalse();
        second.Status.Should().Be(ResultStatus.ConcurrencyConflict);
    }

    /// <summary>
    /// The corruption regression, against the real service rather than a model
    /// of it. A sparse update must leave the person and the whole collection
    /// readable; sending a partial AddressInfo would make both return 500.
    /// </summary>
    [Fact]
    public async Task A_sparse_update_leaves_the_person_and_the_collection_readable()
    {
        var userName = UserName.From("ronaldmundy");

        var loaded = await _repository.GetForUpdateAsync(userName, Token);
        loaded.IsSuccess.Should().BeTrue(loaded.Error);

        var updated = await _repository.UpdateAsync(
            new PersonUpdate
            {
                UserName = userName,
                Concurrency = loaded.Value!.Concurrency,
                FirstName = "Ronald-Sparse",
                Gender = Gender.Unknown,
            },
            Token);

        updated.IsSuccess.Should().BeTrue(updated.Error);

        var detail = await _repository.GetByIdAsync(userName, Token);
        detail.IsSuccess.Should().BeTrue("the person must remain readable");

        var list = await _repository.ListAsync(1, 8, Token);
        list.IsSuccess.Should().BeTrue("the whole collection must remain readable");
    }

    /// <summary>
    /// Confirms the teardown hook itself works, since every other write test
    /// depends on it to leave the sandbox usable.
    /// </summary>
    [Fact]
    public async Task ResetDataSource_restores_a_modified_person()
    {
        var userName = UserName.From("russellwhyte");

        var loaded = await _repository.GetForUpdateAsync(userName, Token);
        await _repository.UpdateAsync(
            new PersonUpdate
            {
                UserName = userName,
                Concurrency = loaded.Value!.Concurrency,
                FirstName = "WillBeReverted",
            },
            Token);

        await ResetDataSourceAsync();

        var reloaded = await _repository.GetByIdAsync(userName, Token);
        reloaded.Value!.Name.First.Should().Be("Russell");
    }
}
