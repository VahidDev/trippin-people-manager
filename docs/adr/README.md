# Architecture decision records

| ADR | Decision |
|---|---|
| [ADR-001](ADR-001-odata-client-choice.md) | Typed `HttpClient` with hand-built OData queries, not `Microsoft.OData.Client` |
| [ADR-002](ADR-002-caching-strategy.md) | Cache reads behind a decorator; never cache ETags |
| [ADR-003](ADR-003-onion-over-flat-layout.md) | Onion over a flat layout |
| [ADR-004](ADR-004-writable-fields-and-addressinfo.md) | Writable fields, and AddressInfo as read-only |

All four rest on behaviour measured against the live TripPin service, not on the metadata document alone. The metadata is frequently a poor guide to what this service actually does: it declares `FirstName` and `LastName` non-nullable without enforcing either, declares `Gender` nullable when writing null silently coerces to `Male`, and says nothing about the session-scoped URLs, the `204`-instead-of-`404` behaviour, or the mandatory `If-Match`.

## Measured service quirks

Every entry was verified against the live endpoint. Each has a named regression test.

| Behaviour | Consequence for this codebase |
|---|---|
| Service root 302-redirects to a `(S(id))` session URL; each fresh hit mints a new session with pristine data | `SessionUriProvider` resolves once and shares it |
| `PATCH`/`PUT` without `If-Match` returns `428` | The token is required on `PersonUpdate`, so a `428` is a defect, never retried |
| `GET` of a missing entity returns `204`, not `404` | `ODataStatusInterpreter` maps it to `NotFound` |
| A `Location` written without its required `City` returns `204`, then makes every read of that person **and of the whole `People` collection** return `500` | `AddressInfo` is read-only and never serialised (ADR-004) |
| Enum literals are inverted: `$filter` needs the fully-qualified form, a `PATCH` body needs the bare name; each rejects the other with `500` | `ODataFilterTranslator` and `PersonMapper` stay separate |
| `Emails: null` returns `500`; `Emails: []` clears the collection | The mapper emits `[]`, never null |
| `Gender: null` returns `204` and silently coerces to `Male` (ordinal 0) | Domain `Gender` is non-nullable, with `Unknown` as the third state |
| An unknown field in `$filter` returns `200` with zero rows, not an error | `PersonFilter` is structured; raw strings never cross the port |
| `PUT` behaves as a merge, not a replace | Only `PATCH` is used |
| A change to the key is accepted with `204` and silently ignored | `UserName` is immutable and never in a payload |
| **`@odata.count` is computed *after* `$top`/`$skip`**, against the specification: `$count=true` reports 20, `$count=true&$top=8` reports 8 | The total is fetched by a separate request that omits paging; reading it from the page would make every query look like "page 1 of 1" |
| Server-driven paging caps pages at 8 rows regardless of `$top`, with an `@odata.nextLink` | `PagedResult` reports the true total so a short page is not mistaken for the end |
