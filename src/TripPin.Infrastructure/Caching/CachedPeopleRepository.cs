using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Application.People.Ports;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using TripPin.Infrastructure.Configuration;

namespace TripPin.Infrastructure.Caching;

/// <summary>
/// Read-through cache decorating <see cref="IPeopleRepository"/>.
/// </summary>
/// <remarks>
/// A decorator rather than a concern inside the use cases, so caching stays a
/// composition-root wiring choice that can be removed by deleting one
/// registration.
/// <para>
/// Two rules this type must honour. First, <see cref="GetForUpdateAsync"/>
/// delegates straight through and never caches: a cached ETag is a stale ETag,
/// and a stale ETag is a failed write. Second, cached values are stored as
/// lazily-awaited tasks, because IMemoryCache's GetOrCreate is not atomic and
/// would otherwise let concurrent misses stampede the service.
/// </para>
/// </remarks>
public sealed class CachedPeopleRepository(
    IPeopleRepository inner,
    IMemoryCache cache,
    PeopleCacheKeys keys,
    IOptions<TripPinOptions> options,
    ILogger<CachedPeopleRepository> logger) : IPeopleRepository
{
    private readonly IPeopleRepository _inner = inner;
    private readonly IMemoryCache _cache = cache;
    private readonly PeopleCacheKeys _keys = keys;
    private readonly CacheOptions _options = options.Value.Cache;
    private readonly ILogger<CachedPeopleRepository> _logger = logger;
    private readonly Lock _gate = new();

    private TimeSpan ListTtl => TimeSpan.FromSeconds(_options.ListTtlSeconds);

    private TimeSpan DetailTtl => TimeSpan.FromSeconds(_options.DetailTtlSeconds);

    public Task<Result<PagedResult<PersonSummary>>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ReadThroughAsync(
            PeopleCacheKeys.List(page, pageSize),
            ListTtl,
            isListEntry: true,
            () => _inner.ListAsync(page, pageSize, cancellationToken));

    public Task<Result<PagedResult<PersonSummary>>> SearchAsync(
        PersonFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ReadThroughAsync(
            PeopleCacheKeys.Search(filter, page, pageSize),
            ListTtl,
            isListEntry: true,
            () => _inner.SearchAsync(filter, page, pageSize, cancellationToken));

    public Task<Result<Person>> GetByIdAsync(
        UserName userName,
        CancellationToken cancellationToken) =>
        ReadThroughAsync(
            PeopleCacheKeys.Detail(userName),
            DetailTtl,
            isListEntry: false,
            () => _inner.GetByIdAsync(userName, cancellationToken));

    /// <summary>Always bypasses the cache. See the remarks on this class.</summary>
    public Task<Result<Person>> GetForUpdateAsync(
        UserName userName,
        CancellationToken cancellationToken) =>
        _inner.GetForUpdateAsync(userName, cancellationToken);

    public async Task<Result<ConcurrencyToken>> UpdateAsync(
        PersonUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var result = await _inner.UpdateAsync(update, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result;
        }

        // The person's own entry goes by key; every list and search entry goes
        // with the generation token, since their contents may have shifted.
        _cache.Remove(PeopleCacheKeys.Detail(update.UserName));
        _keys.InvalidateLists();

        _logger.LogDebug(
            "Evicted the cached entry for {UserName} and every cached list.",
            update.UserName.Value);

        return result;
    }

    private async Task<Result<T>> ReadThroughAsync<T>(
        string cacheKey,
        TimeSpan timeToLive,
        bool isListEntry,
        Func<Task<Result<T>>> read)
    {
        if (!_options.Enabled)
        {
            return await read().ConfigureAwait(false);
        }

        var pending = GetOrAdd(cacheKey, timeToLive, isListEntry, read);
        var result = await pending.Value.ConfigureAwait(false);

        // A failure must not occupy the entry for the whole TTL, or one blip
        // becomes a minute of cached errors.
        if (!result.IsSuccess)
        {
            _cache.Remove(cacheKey);
        }

        return result;
    }

    /// <summary>
    /// Double-checked insert of a <see cref="Lazy{T}"/> wrapping the read.
    /// </summary>
    /// <remarks>
    /// The lock is held only while the Lazy is created and stored, never across
    /// the await, so concurrent misses on one key share a single request
    /// without any caller blocking on another's I/O.
    /// </remarks>
    private Lazy<Task<Result<T>>> GetOrAdd<T>(
        string cacheKey,
        TimeSpan timeToLive,
        bool isListEntry,
        Func<Task<Result<T>>> read)
    {
        if (_cache.TryGetValue(cacheKey, out Lazy<Task<Result<T>>>? cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {CacheKey}.", cacheKey);

            return cached;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            _logger.LogDebug("Cache miss for {CacheKey}.", cacheKey);

            var pending = new Lazy<Task<Result<T>>>(
                read,
                LazyThreadSafetyMode.ExecutionAndPublication);

            var entryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = timeToLive,
                Size = 1,
            };

            if (isListEntry)
            {
                entryOptions.AddExpirationToken(_keys.ListGeneration);
            }

            _cache.Set(cacheKey, pending, entryOptions);

            return pending;
        }
    }
}
