using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Application.People.Ports;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using TripPin.Infrastructure.Caching;
using TripPin.Infrastructure.Configuration;
using Xunit;

namespace TripPin.Infrastructure.Tests.Caching;

/// <summary>
/// A controllable inner repository. Counts calls and can be held open, which
/// is what makes the stampede test possible.
/// </summary>
internal sealed class ControllableRepository : IPeopleRepository
{
    private readonly TaskCompletionSource _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _listCalls;
    private int _searchCalls;
    private int _getByIdCalls;
    private int _getForUpdateCalls;

    public int ListCalls => Volatile.Read(ref _listCalls);

    public int SearchCalls => Volatile.Read(ref _searchCalls);

    public int GetByIdCalls => Volatile.Read(ref _getByIdCalls);

    public int GetForUpdateCalls => Volatile.Read(ref _getForUpdateCalls);

    public int UpdateCalls { get; private set; }

    /// <summary>When set, reads block until <see cref="Release"/> is called.</summary>
    public bool Block { get; set; }

    public Result<PagedResult<PersonSummary>> ListResult { get; set; } =
        Result<PagedResult<PersonSummary>>.Success(new PagedResult<PersonSummary>([], 0, 1, 8));

    public Result<ConcurrencyToken> UpdateResult { get; set; } =
        Result<ConcurrencyToken>.Success(ConcurrencyToken.From("W/\"after\""));

    /// <summary>Distinct per call, so a cached value is recognisable as stale.</summary>
    public Func<Result<Person>> PersonFactory { get; set; } =
        () => Result<Person>.Success(TestPeople.Russell($"W/\"{Guid.NewGuid()}\""));

    public void Release() => _gate.TrySetResult();

    public async Task<Result<PagedResult<PersonSummary>>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listCalls);
        await WaitIfBlockedAsync().ConfigureAwait(false);

        return ListResult;
    }

    public async Task<Result<PagedResult<PersonSummary>>> SearchAsync(
        PersonFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _searchCalls);
        await WaitIfBlockedAsync().ConfigureAwait(false);

        return ListResult;
    }

    public async Task<Result<Person>> GetByIdAsync(
        UserName userName,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _getByIdCalls);
        await WaitIfBlockedAsync().ConfigureAwait(false);

        return PersonFactory();
    }

    public Task<Result<Person>> GetForUpdateAsync(
        UserName userName,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _getForUpdateCalls);

        return Task.FromResult(PersonFactory());
    }

    public Task<Result<ConcurrencyToken>> UpdateAsync(
        PersonUpdate update,
        CancellationToken cancellationToken)
    {
        UpdateCalls++;

        return Task.FromResult(UpdateResult);
    }

    private Task WaitIfBlockedAsync() => Block ? _gate.Task : Task.CompletedTask;
}

internal static class TestPeople
{
    public static Person Russell(string token = "W/\"1\"") => Person.Create(
        UserName.From("russellwhyte"),
        PersonName.From("Russell", "Whyte"),
        [],
        [],
        Gender.Male,
        ConcurrencyToken.From(token));

    public static PersonUpdate Update() => new()
    {
        UserName = UserName.From("russellwhyte"),
        Concurrency = ConcurrencyToken.From("W/\"caller\""),
        FirstName = "Russ",
    };
}

public sealed class CachedPeopleRepositoryTests : IDisposable
{
    private readonly ControllableRepository _inner = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly PeopleCacheKeys _keys = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _cache.Dispose();
        _keys.Dispose();
    }

    private CachedPeopleRepository Create(bool enabled = true) => new(
        _inner,
        _cache,
        _keys,
        Options.Create(new TripPinOptions
        {
            Cache = new CacheOptions
            {
                Enabled = enabled,
                ListTtlSeconds = 60,
                DetailTtlSeconds = 120,
                SizeLimit = 256,
            },
        }),
        NullLogger<CachedPeopleRepository>.Instance);

    // -----------------------------------------------------------------
    // Hit and miss
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_repeated_list_is_served_from_cache()
    {
        var repository = Create();

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(1, 8, Token);

        _inner.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task Different_pages_are_cached_separately()
    {
        var repository = Create();

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(2, 8, Token);

        _inner.ListCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_repeated_detail_read_is_served_from_cache()
    {
        var repository = Create();

        var first = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        var second = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        _inner.GetByIdCalls.Should().Be(1);
        second.Value!.Concurrency.Should().Be(first.Value!.Concurrency);
    }

    [Fact]
    public async Task Different_people_are_cached_separately()
    {
        var repository = Create();

        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        await repository.GetByIdAsync(UserName.From("scottketchum"), Token);

        _inner.GetByIdCalls.Should().Be(2);
    }

    /// <summary>
    /// Structurally identical searches must share an entry, or the cache never
    /// hits for the one operation a user repeats most.
    /// </summary>
    [Fact]
    public async Task Structurally_identical_searches_share_an_entry()
    {
        var repository = Create();

        await repository.SearchAsync(new PersonFilter { NameContains = "russ" }, 1, 8, Token);
        await repository.SearchAsync(new PersonFilter { NameContains = "russ" }, 1, 8, Token);

        _inner.SearchCalls.Should().Be(1);
    }

    [Fact]
    public async Task Different_filters_are_cached_separately()
    {
        var repository = Create();

        await repository.SearchAsync(new PersonFilter { NameContains = "russ" }, 1, 8, Token);
        await repository.SearchAsync(new PersonFilter { NameContains = "scott" }, 1, 8, Token);

        _inner.SearchCalls.Should().Be(2);
    }

    // -----------------------------------------------------------------
    // The freshness rule
    // -----------------------------------------------------------------

    /// <summary>
    /// The most important behaviour of the decorator. A cached ETag is a stale
    /// ETag, and a stale ETag is a 412 on the next write.
    /// </summary>
    [Fact]
    public async Task GetForUpdateAsync_always_bypasses_the_cache()
    {
        var repository = Create();

        await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);
        await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);
        await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);

        _inner.GetForUpdateCalls.Should().Be(3);
    }

    [Fact]
    public async Task GetForUpdateAsync_returns_a_fresh_token_every_time()
    {
        var repository = Create();

        var first = await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);
        var second = await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);

        second.Value!.Concurrency.Should().NotBe(first.Value!.Concurrency);
    }

    /// <summary>
    /// A prior cached display read must not leak into the update path.
    /// </summary>
    [Fact]
    public async Task GetForUpdateAsync_ignores_an_entry_cached_by_a_display_read()
    {
        var repository = Create();

        var cached = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        var fresh = await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);

        fresh.Value!.Concurrency.Should().NotBe(cached.Value!.Concurrency);
        _inner.GetForUpdateCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetForUpdateAsync_does_not_populate_the_cache()
    {
        var repository = Create();

        await repository.GetForUpdateAsync(UserName.From("russellwhyte"), Token);
        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        _inner.GetByIdCalls.Should().Be(1);
    }

    // -----------------------------------------------------------------
    // Invalidation
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_successful_update_evicts_the_persons_own_entry()
    {
        var repository = Create();

        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        await repository.UpdateAsync(TestPeople.Update(), Token);
        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        _inner.GetByIdCalls.Should().Be(2);
    }

    /// <summary>
    /// Lists go through the shared generation token, because an edit can change
    /// what any page contains, not just the page the person was on.
    /// </summary>
    [Fact]
    public async Task A_successful_update_evicts_every_cached_list()
    {
        var repository = Create();

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(2, 8, Token);
        await repository.SearchAsync(new PersonFilter { NameContains = "russ" }, 1, 8, Token);

        await repository.UpdateAsync(TestPeople.Update(), Token);

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(2, 8, Token);
        await repository.SearchAsync(new PersonFilter { NameContains = "russ" }, 1, 8, Token);

        _inner.ListCalls.Should().Be(4);
        _inner.SearchCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_update_evicts_nothing()
    {
        var repository = Create();
        _inner.UpdateResult = Result<ConcurrencyToken>.Conflict("stale");

        await repository.ListAsync(1, 8, Token);
        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        await repository.UpdateAsync(TestPeople.Update(), Token);

        await repository.ListAsync(1, 8, Token);
        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        _inner.ListCalls.Should().Be(1);
        _inner.GetByIdCalls.Should().Be(1);
    }

    /// <summary>
    /// Invalidation must not disable caching from then on: entries created
    /// after an update register against the new generation and must survive.
    /// </summary>
    [Fact]
    public async Task Caching_still_works_after_an_invalidation()
    {
        var repository = Create();

        await repository.ListAsync(1, 8, Token);
        await repository.UpdateAsync(TestPeople.Update(), Token);

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(1, 8, Token);

        _inner.ListCalls.Should().Be(2);
    }

    // -----------------------------------------------------------------
    // Stampede protection
    // -----------------------------------------------------------------

    /// <summary>
    /// IMemoryCache's GetOrCreate is not atomic, so concurrent misses on one
    /// key would each issue their own request. Caching a lazily-awaited task
    /// rather than a value is what collapses them into one.
    /// </summary>
    [Fact]
    public async Task Concurrent_misses_on_one_key_call_the_inner_repository_once()
    {
        var repository = Create();
        _inner.Block = true;

        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => repository.ListAsync(1, 8, Token), Token))
            .ToArray();

        _inner.Release();
        await Task.WhenAll(callers);

        _inner.ListCalls.Should().Be(1);
        callers.Should().OnlyContain(caller => caller.Result.IsSuccess);
    }

    [Fact]
    public async Task Concurrent_misses_on_one_detail_key_call_the_inner_repository_once()
    {
        var repository = Create();
        _inner.Block = true;

        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(
                () => repository.GetByIdAsync(UserName.From("russellwhyte"), Token), Token))
            .ToArray();

        _inner.Release();
        await Task.WhenAll(callers);

        _inner.GetByIdCalls.Should().Be(1);
    }

    /// <summary>
    /// Every concurrent caller must observe the same instance, not just an
    /// equal one, proving the request really was shared.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_all_observe_the_same_result()
    {
        var repository = Create();
        _inner.Block = true;

        var callers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(
                () => repository.GetByIdAsync(UserName.From("russellwhyte"), Token), Token))
            .ToArray();

        _inner.Release();
        var results = await Task.WhenAll(callers);

        results.Should().OnlyContain(result =>
            ReferenceEquals(result.Value, results[0].Value));
    }

    // -----------------------------------------------------------------
    // Failures and the off switch
    // -----------------------------------------------------------------

    /// <summary>
    /// One blip must not become a minute of cached errors.
    /// </summary>
    [Fact]
    public async Task A_failure_is_not_cached()
    {
        var repository = Create();
        _inner.ListResult = Result<PagedResult<PersonSummary>>.Failure(
            ResultStatus.TransportFailure, "circuit open");

        await repository.ListAsync(1, 8, Token);

        _inner.ListResult = Result<PagedResult<PersonSummary>>.Success(
            new PagedResult<PersonSummary>([], 0, 1, 8));

        var second = await repository.ListAsync(1, 8, Token);

        _inner.ListCalls.Should().Be(2);
        second.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_not_found_detail_read_is_not_cached()
    {
        var repository = Create();
        _inner.PersonFactory = () => Result<Person>.NotFound("gone");

        await repository.GetByIdAsync(UserName.From("nobody"), Token);
        await repository.GetByIdAsync(UserName.From("nobody"), Token);

        _inner.GetByIdCalls.Should().Be(2);
    }

    /// <summary>
    /// Caching is a composition-root choice, so switching it off must leave a
    /// plain pass-through rather than changing behaviour.
    /// </summary>
    [Fact]
    public async Task Every_read_reaches_the_service_when_caching_is_disabled()
    {
        var repository = Create(enabled: false);

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(1, 8, Token);
        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        _inner.ListCalls.Should().Be(2);
        _inner.GetByIdCalls.Should().Be(2);
    }

    [Fact]
    public async Task An_update_is_always_delegated_regardless_of_caching()
    {
        var repository = Create();

        var result = await repository.UpdateAsync(TestPeople.Update(), Token);

        result.IsSuccess.Should().BeTrue();
        _inner.UpdateCalls.Should().Be(1);
    }
}

public sealed class PeopleCacheKeysTests
{
    [Fact]
    public void Detail_keys_are_distinct_per_person()
    {
        PeopleCacheKeys.Detail(UserName.From("russellwhyte"))
            .Should().NotBe(PeopleCacheKeys.Detail(UserName.From("scottketchum")));
    }

    [Fact]
    public void List_keys_are_distinct_per_page()
    {
        PeopleCacheKeys.List(1, 8).Should().NotBe(PeopleCacheKeys.List(2, 8));
        PeopleCacheKeys.List(1, 8).Should().NotBe(PeopleCacheKeys.List(1, 16));
    }

    [Fact]
    public void Search_keys_are_built_from_the_filter_components()
    {
        PeopleCacheKeys.Search(new PersonFilter { NameContains = "russ" }, 1, 8)
            .Should().Be(PeopleCacheKeys.Search(new PersonFilter { NameContains = "russ" }, 1, 8));

        PeopleCacheKeys.Search(new PersonFilter { NameContains = "russ" }, 1, 8)
            .Should().NotBe(PeopleCacheKeys.Search(new PersonFilter { Gender = Gender.Male }, 1, 8));
    }

    [Fact]
    public void A_detail_key_never_collides_with_a_list_key()
    {
        PeopleCacheKeys.Detail(UserName.From("russellwhyte"))
            .Should().NotBe(PeopleCacheKeys.List(1, 8));
    }

    /// <summary>
    /// Swapping before cancelling is what lets an entry created during an
    /// invalidation survive rather than being evicted as it is written.
    /// </summary>
    [Fact]
    public void InvalidateLists_replaces_the_generation_rather_than_leaving_it_cancelled()
    {
        using var keys = new PeopleCacheKeys();

        var before = keys.ListGeneration;
        keys.InvalidateLists();
        var after = keys.ListGeneration;

        before.HasChanged.Should().BeTrue();
        after.HasChanged.Should().BeFalse();
    }
}
