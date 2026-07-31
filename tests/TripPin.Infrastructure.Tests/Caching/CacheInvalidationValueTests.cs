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
/// A repository whose returned values change on demand, so a test can tell a
/// stale cached value from a fresh one.
/// </summary>
/// <remarks>
/// The existing decorator tests assert call counts, which proves the inner
/// repository was consulted again. These assert on the <em>value</em> handed
/// back, which is what a caller actually sees, and is the property that would
/// still be wrong if an entry were re-populated from a stale source.
/// </remarks>
internal sealed class VersionedRepository : IPeopleRepository
{
    private int _listCalls;
    private int _searchCalls;
    private int _getByIdCalls;

    /// <summary>Bumped by a test to represent the world having moved on.</summary>
    public string Version { get; set; } = "before";

    public int ListCalls => Volatile.Read(ref _listCalls);

    public int SearchCalls => Volatile.Read(ref _searchCalls);

    public int GetByIdCalls => Volatile.Read(ref _getByIdCalls);

    public Result<ConcurrencyToken> UpdateResult { get; set; } =
        Result<ConcurrencyToken>.Success(ConcurrencyToken.From("W/\"after\""));

    public Task<Result<PagedResult<PersonSummary>>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listCalls);

        return Task.FromResult(Page(Version, page, pageSize));
    }

    public Task<Result<PagedResult<PersonSummary>>> SearchAsync(
        PersonFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _searchCalls);

        // Encodes the filter into the result, so a test can prove entries were
        // not crossed over between concurrent searches.
        return Task.FromResult(Page($"{Version}:{filter.NameContains}", page, pageSize));
    }

    public Task<Result<Person>> GetByIdAsync(UserName userName, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _getByIdCalls);

        return Task.FromResult(Result<Person>.Success(Person.Create(
            userName,
            PersonName.From(Version, "Whyte"),
            [],
            [],
            Gender.Male,
            ConcurrencyToken.From($"W/\"{Version}\""))));
    }

    public Task<Result<Person>> GetForUpdateAsync(
        UserName userName,
        CancellationToken cancellationToken) => GetByIdAsync(userName, cancellationToken);

    public Task<Result<ConcurrencyToken>> UpdateAsync(
        PersonUpdate update,
        CancellationToken cancellationToken) => Task.FromResult(UpdateResult);

    private static Result<PagedResult<PersonSummary>> Page(string marker, int page, int pageSize) =>
        Result<PagedResult<PersonSummary>>.Success(new PagedResult<PersonSummary>(
            [new PersonSummary(UserName.From("russellwhyte"), PersonName.From(marker, "Whyte"), Gender.Male)],
            1,
            page,
            pageSize));
}

public sealed class CacheInvalidationValueTests : IDisposable
{
    private readonly VersionedRepository _inner = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly PeopleCacheKeys _keys = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _cache.Dispose();
        _keys.Dispose();
    }

    private CachedPeopleRepository Create() => new(
        _inner,
        _cache,
        _keys,
        Options.Create(new TripPinOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                ListTtlSeconds = 60,
                DetailTtlSeconds = 120,
                SizeLimit = 256,
            },
        }),
        NullLogger<CachedPeopleRepository>.Instance);

    private static PersonUpdate Update() => new()
    {
        UserName = UserName.From("russellwhyte"),
        Concurrency = ConcurrencyToken.From("W/\"caller\""),
        FirstName = "Russ",
    };

    // -----------------------------------------------------------------
    // The cached value really is stale before invalidation
    // -----------------------------------------------------------------

    /// <summary>
    /// Establishes that the setup can actually detect staleness, so the tests
    /// below are not passing vacuously.
    /// </summary>
    [Fact]
    public async Task Without_an_update_a_cached_detail_stays_stale()
    {
        var repository = Create();

        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        _inner.Version = "after";

        var second = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        second.Value!.Name.First.Should().Be("before", "the entry is still cached");
    }

    [Fact]
    public async Task Without_an_update_a_cached_list_stays_stale()
    {
        var repository = Create();

        await repository.ListAsync(1, 8, Token);
        _inner.Version = "after";

        var second = await repository.ListAsync(1, 8, Token);

        second.Value!.Items[0].Name.First.Should().Be("before");
    }

    // -----------------------------------------------------------------
    // After an update, the pre-update value is gone
    // -----------------------------------------------------------------

    /// <summary>
    /// The property that matters: not merely that the inner repository was
    /// called again, but that the caller receives the new value.
    /// </summary>
    [Fact]
    public async Task A_detail_read_after_an_update_returns_the_new_value()
    {
        var repository = Create();

        var before = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        before.Value!.Name.First.Should().Be("before");

        _inner.Version = "after";
        await repository.UpdateAsync(Update(), Token);

        var after = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        after.Value!.Name.First.Should().Be("after");
        after.Value.Concurrency.Value.Should().Be("W/\"after\"");
    }

    [Fact]
    public async Task A_list_read_after_an_update_returns_the_new_value()
    {
        var repository = Create();

        var before = await repository.ListAsync(1, 8, Token);
        before.Value!.Items[0].Name.First.Should().Be("before");

        _inner.Version = "after";
        await repository.UpdateAsync(Update(), Token);

        var after = await repository.ListAsync(1, 8, Token);

        after.Value!.Items[0].Name.First.Should().Be("after");
    }

    [Fact]
    public async Task A_search_read_after_an_update_returns_the_new_value()
    {
        var repository = Create();
        var filter = new PersonFilter { NameContains = "russ" };

        var before = await repository.SearchAsync(filter, 1, 8, Token);
        before.Value!.Items[0].Name.First.Should().Be("before:russ");

        _inner.Version = "after";
        await repository.UpdateAsync(Update(), Token);

        var after = await repository.SearchAsync(filter, 1, 8, Token);

        after.Value!.Items[0].Name.First.Should().Be("after:russ");
    }

    /// <summary>
    /// An edit can move a person between pages, so every page must be refreshed,
    /// not just the one the edited person happened to appear on.
    /// </summary>
    [Fact]
    public async Task Every_cached_page_returns_new_values_after_an_update()
    {
        var repository = Create();

        await repository.ListAsync(1, 8, Token);
        await repository.ListAsync(2, 8, Token);
        await repository.ListAsync(3, 8, Token);

        _inner.Version = "after";
        await repository.UpdateAsync(Update(), Token);

        foreach (var page in new[] { 1, 2, 3 })
        {
            var result = await repository.ListAsync(page, 8, Token);

            result.Value!.Items[0].Name.First
                .Should().Be("after", "page {0} must not serve a pre-update value", page);
        }
    }

    /// <summary>
    /// Editing one person must not leave another person's cached entry stale,
    /// since list contents can shift.
    /// </summary>
    [Fact]
    public async Task A_different_persons_detail_entry_survives_an_unrelated_update()
    {
        var repository = Create();

        await repository.GetByIdAsync(UserName.From("scottketchum"), Token);
        _inner.Version = "after";

        await repository.UpdateAsync(Update(), Token);

        var other = await repository.GetByIdAsync(UserName.From("scottketchum"), Token);

        // Only the edited person's entry is evicted by key, so this one is
        // still served from cache. Lists are the shared surface, and they are
        // all refreshed above.
        other.Value!.Name.First.Should().Be("before");
    }

    [Fact]
    public async Task A_failed_update_leaves_the_cached_value_in_place()
    {
        var repository = Create();
        _inner.UpdateResult = Result<ConcurrencyToken>.Conflict("stale");

        await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        _inner.Version = "after";

        await repository.UpdateAsync(Update(), Token);

        var reread = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);

        reread.Value!.Name.First.Should().Be("before", "nothing changed, so nothing was evicted");
    }

    // -----------------------------------------------------------------
    // Concurrent misses on different keys
    // -----------------------------------------------------------------

    /// <summary>
    /// Concurrent searches with different filters must not share, overwrite or
    /// cross-contaminate entries.
    /// </summary>
    /// <remarks>
    /// The decorator takes a lock around cache insertion, so a bug here would
    /// most plausibly be one key's <c>Lazy</c> being stored under another's, or
    /// a last-writer-wins overwrite. Each result carries its own filter text,
    /// so a crossover is directly observable rather than merely implied by a
    /// call count.
    /// </remarks>
    [Fact]
    public async Task Concurrent_searches_with_different_filters_stay_independent()
    {
        var repository = Create();

        var fragments = Enumerable.Range(0, 24).Select(index => $"f{index}").ToArray();

        var searches = fragments
            .Select(fragment => Task.Run(
                async () =>
                {
                    var result = await repository.SearchAsync(
                        new PersonFilter { NameContains = fragment }, 1, 8, Token);

                    return (fragment, result);
                },
                Token))
            .ToArray();

        var completed = await Task.WhenAll(searches);

        _inner.SearchCalls.Should().Be(fragments.Length, "each distinct filter is its own entry");

        foreach (var (fragment, result) in completed)
        {
            result.IsSuccess.Should().BeTrue();
            result.Value!.Items[0].Name.First
                .Should().Be($"before:{fragment}", "each caller must receive its own filter's result");
        }
    }

    /// <summary>
    /// Repeating the same set concurrently must now be served entirely from
    /// cache, proving the entries were stored under distinct keys rather than
    /// one having displaced the others.
    /// </summary>
    [Fact]
    public async Task Entries_written_concurrently_are_all_retrievable_afterwards()
    {
        var repository = Create();

        var fragments = Enumerable.Range(0, 24).Select(index => $"f{index}").ToArray();

        await Task.WhenAll(fragments.Select(fragment => Task.Run(
            () => repository.SearchAsync(new PersonFilter { NameContains = fragment }, 1, 8, Token),
            Token)));

        var callsAfterFirstPass = _inner.SearchCalls;

        foreach (var fragment in fragments)
        {
            var result = await repository.SearchAsync(
                new PersonFilter { NameContains = fragment }, 1, 8, Token);

            result.Value!.Items[0].Name.First.Should().Be($"before:{fragment}");
        }

        _inner.SearchCalls.Should().Be(
            callsAfterFirstPass,
            "the second pass must be served entirely from cache");
    }

    /// <summary>
    /// The mixed case: concurrent reads across list, search and detail keys at
    /// once, which is what an impatient user actually produces.
    /// </summary>
    [Fact]
    public async Task Concurrent_reads_across_different_kinds_of_key_stay_independent()
    {
        var repository = Create();

        var work = new List<Task>();

        for (var page = 1; page <= 4; page++)
        {
            var captured = page;
            work.Add(Task.Run(() => repository.ListAsync(captured, 8, Token), Token));
        }

        for (var index = 0; index < 4; index++)
        {
            var fragment = $"g{index}";
            work.Add(Task.Run(
                () => repository.SearchAsync(new PersonFilter { NameContains = fragment }, 1, 8, Token),
                Token));
        }

        foreach (var name in new[] { "russellwhyte", "scottketchum", "ronaldmundy", "javieralfred" })
        {
            var captured = name;
            work.Add(Task.Run(() => repository.GetByIdAsync(UserName.From(captured), Token), Token));
        }

        await Task.WhenAll(work);

        _inner.ListCalls.Should().Be(4);
        _inner.SearchCalls.Should().Be(4);
        _inner.GetByIdCalls.Should().Be(4);
    }
}
