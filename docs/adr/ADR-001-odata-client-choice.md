# ADR-001: Typed `HttpClient` with hand-built OData queries, not `Microsoft.OData.Client`

**Status:** Accepted
**Date:** 2026-07-31

## Context

Two viable clients. `Microsoft.OData.Client` generates a typed proxy from `$metadata`, gives LINQ-to-OData, and tracks ETags automatically via `DataServiceContext`. The alternative is a typed `HttpClient` with a small query builder and manual DTO mapping.

## Decision

Typed `HttpClient`.

## Rationale

1. **`DataServiceContext` is stateful and not thread-safe.** It is a unit-of-work with an entity tracker, so supporting overlapping searches means a context per operation, which discards the change-tracking that is its main selling point. A stateless repository is thread-safe by construction.
2. **It does not integrate with `IHttpClientFactory`.** It owns its request pipeline, so injecting a Polly handler chain means working through `Configurations.RequestPipeline` and bypassing the factory. Since resilience and lifetime management were stated requirements, this alone is close to decisive.
3. **Every quirk needs HTTP-layer interception.** Session redirects, `204`-as-not-found, and `428` mapping all live naturally in a `DelegatingHandler` or a response interpreter. Through the typed client they are awkward at best.
4. **The codegen never amortises.** The app touches one entity set with four operations. We would generate the full model (containment, media streams, bound functions) to use a sliver of it, then fight it on the parts that matter.

## Consequences

We hand-write query strings, which is a real escaping risk. Mitigated by confining construction to `ODataQueryBuilder`, keeping raw OData strings out of the port entirely (`PersonFilter` is structured), and covering the builder with golden-string tests, including the fully-qualified enum form that a naive `Gender eq 'Female'` gets a `500` for. We also hand-map DTOs, which is mechanical and testable.

If this ever grew to cover Trips, Photos, and bound functions, the calculus would flip and this decision should be revisited.
