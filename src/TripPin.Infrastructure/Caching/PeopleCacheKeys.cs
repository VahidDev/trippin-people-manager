using Microsoft.Extensions.Primitives;
using TripPin.Application.People.Models;
using TripPin.Domain.People;

namespace TripPin.Infrastructure.Caching;

/// <summary>
/// Canonical cache keys, plus the generation token used for bulk eviction.
/// </summary>
/// <remarks>
/// IMemoryCache has no tag-based eviction, so every list entry is registered
/// against a shared CancellationChangeToken. Cancelling and replacing that
/// token evicts all list entries at once, which is what an update needs.
/// Detail entries are evicted directly by key.
/// <para>
/// Registered as a singleton, because a per-scope generation token would evict
/// nothing that another scope had cached.
/// </para>
/// </remarks>
public sealed class PeopleCacheKeys : IDisposable
{
    private readonly Lock _gate = new();

    private CancellationTokenSource _generation = new();

    public static string Detail(UserName userName)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return $"people:detail:{userName.Value}";
    }

    public static string List(int page, int pageSize) => $"people:list:{page}:{pageSize}";

    /// <summary>
    /// Builds a key from the filter's component parts rather than its hash, so
    /// two structurally identical searches share an entry.
    /// </summary>
    public static string Search(PersonFilter filter, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return $"people:search:{filter.NameContains}|{filter.Gender}|{filter.EmailContains}"
            + $":{page}:{pageSize}";
    }

    /// <summary>Change token every cached list entry is registered against.</summary>
    public IChangeToken ListGeneration
    {
        get
        {
            lock (_gate)
            {
                return new CancellationChangeToken(_generation.Token);
            }
        }
    }

    public void InvalidateLists()
    {
        CancellationTokenSource superseded;

        // Swapped before cancelling so an entry created concurrently registers
        // against the incoming generation and survives, rather than being
        // evicted the moment it is written.
        lock (_gate)
        {
            superseded = _generation;
            _generation = new CancellationTokenSource();
        }

        superseded.Cancel();
        superseded.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _generation.Dispose();
        }
    }
}
