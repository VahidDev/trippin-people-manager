# ADR-004: Writable fields, and AddressInfo as read-only

**Status:** Accepted
**Date:** 2026-07-31

## Context

Before fixing `AddressInfo` as read-only, we needed to know whether `Person` had any other non-scalar field worth exercising as writable, so that the update feature is not only proving it can PATCH plain strings. Every claim below was measured against the live service rather than read off the metadata.

## Decision

The writable surface is **`FirstName`, `LastName`, `Emails` and `Gender`**. `AddressInfo` is read-only: displayed on the detail screen, absent from the edit screen. `UserName` and `Concurrency` are not writable.

## Findings

| Field | Kind | Writable? | What it exercises |
|---|---|---|---|
| `FirstName` / `LastName` | scalar string | yes | baseline |
| `Emails` | `Collection(Edm.String)` | yes | collection replace semantics, empty vs null, client-side element validation |
| `Gender` | enum | yes | enum literal mapping, and a form asymmetry (below) |
| `AddressInfo` | `Collection(Location)` | technically | a partial write silently corrupts the record |
| `UserName`, `Concurrency` | key / computed | no | silently ignored / server-managed |

### Emails is the non-scalar worth having

- PATCH **replaces** the whole collection, it does not merge or append. Clean, predictable semantics.
- `[]` clears it (`204`). **`null` returns `500`.** So "remove all emails" must serialize as an empty array, never null.
- `["not-an-email"]` is accepted with a `204`. The server does no element validation at all, so the `EmailAddress` value object is doing real work rather than decorating a string.
- A wrong element type (`[123]`) is a `500`.

### Gender is a worthwhile second one

The enum literal form is **inverted** between reads and writes, which is easy to get backwards:

- In `$filter`, the enum **must be fully qualified**: `Gender eq Microsoft...PersonGender'Female'` works, bare `'Female'` is a `500`.
- In a PATCH body, it is **exactly inverted**: bare `"Female"` works, the qualified form is a `500`.

Unlike strings, the enum *is* validated (`"Banana"` and numeric `1` both `500`). One catch: `"Gender": null` returns `204` but silently coerces to `Male`, since that is ordinal 0. Nullable in the metadata, not nullable in practice.

### AddressInfo: read-only is not merely acceptable, it is the right call

A PATCH that omits the required `City` returns **`204 No Content`**, then permanently corrupts the record. Every subsequent read fails:

```
The property 'City[Nullable=False]' ... has a null value, which is not allowed.
```

The blast radius is the whole entity set: one poisoned row makes `GET People` return `500`, so the list screen dies too, not just that person's detail page. It is recoverable only by a blind PATCH (you cannot read the record to repair it), or `ResetDataSource`. A successful-looking write that bricks the collection is not something to put behind a console form.

## Consequences

Three deltas to the architecture follow from this:

1. **Sparse PATCH becomes a hard rule, not a preference.** `PersonMapper` emits only fields the user actually edited. Sending the full entity would re-serialize `AddressInfo` on every save, turning a read-only field into a standing corruption risk. This also sidesteps the open-type behaviour (a junk `"Nonsense"` sub-property inside `Location` persists happily).
2. **The `ODataFilterTranslator` / `PersonMapper` split is load-bearing.** Read-path and write-path enum literals are genuinely inverted, so a single shared "serialize a Gender" helper would be wrong on one side by construction. Two components, two golden-string tests.
3. **Domain models `Gender` as non-nullable** with `Unknown` as the explicit third state, and the mapper never emits null for it. Likewise `Emails` clears as `[]`. Both go in `Infrastructure.Tests/QuirkHandling` alongside a regression test asserting we never emit a partial `Location`.

Net: the update feature exercises a scalar pair, a primitive collection with replace semantics and a null/empty trap, and a validated enum with direction-dependent encoding. That is a real surface, not just proving we can PATCH a string.
