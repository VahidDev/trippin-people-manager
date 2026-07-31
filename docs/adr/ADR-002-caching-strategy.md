# ADR-002: Cache reads behind a decorator; never cache ETags

**Status:** Accepted
**Date:** 2026-07-31

## Context

A console menu re-reads the same small dataset repeatedly. `IMemoryCache` is cheap. But the service demands `If-Match` on every write and returns `412` for a stale token.

## Decision

Cache reads in an `IPeopleRepository` decorator with short TTLs (60s lists, 120s details) and explicit invalidation on update. Expose `GetForUpdateAsync` as a distinct, never-cached path for the read-before-write.

## Rationale

The decorator keeps `IMemoryCache` out of Application entirely, so caching is a composition-root wiring choice and can be removed by deleting one registration. Splitting the read-for-display path from the read-for-update path makes the ETag freshness rule structural rather than a comment someone has to remember.

## Consequences

Lists can be up to 60s stale, which is acceptable for browsing. Updates always cost an extra round trip, which is correct and unavoidable given `428`. Invalidation needs a `CancellationChangeToken` generation pattern because `IMemoryCache` lacks tag eviction, and cached values are `Lazy<Task<T>>` to prevent stampedes.

Accepted alternative if this proves fiddly: drop list caching entirely and keep only the freshness-critical paths, since 20 records over a fast API do not really need it.

## Supporting detail

The tradeoff, stated plainly. Caching buys real latency wins on a menu loop where the user pages back and forth over 20 records, and cuts load on a shared sandbox. The cost is staleness, which is normally benign for a list. It is **not** benign for ETags: serving a cached entity as the source of an `If-Match` value produces a `412` the moment anything else has touched that record. Hence `GetForUpdateAsync` bypasses cache unconditionally. A stale list is a cosmetic annoyance; a stale ETag is a failed write.

Given this service is sandboxed per session and effectively single-user, caching is a modest win. It earns its place mainly because the decorator makes it nearly free to add and trivial to remove, not because the workload demands it.

Two implementation points that are easy to get wrong:

- **`GetOrCreate` is not atomic.** Concurrent misses on the same key invoke the factory more than once. Cache a `Lazy<Task<T>>` (or `AsyncLazy<T>`) as the *value*, so the first caller creates the task and the rest await it. That gives stampede protection without a lock per request.
- **`IMemoryCache` has no tag eviction.** For "invalidate every list result after an update", register all list entries against a shared `CancellationChangeToken`, then cancel and replace that token on write. Detail entries are evicted directly by key.
