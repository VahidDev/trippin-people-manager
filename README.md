# TripPin People Manager

A console application for browsing, searching and editing people in the public
[TripPin OData v4 sample service](https://services.odata.org/v4/TripPinServiceRW/),
built with Onion architecture on .NET 10.

The service is a deliberately awkward integration target: session-scoped URLs,
mandatory ETags, `204` instead of `404`, and a write that reports success while
corrupting the record. Most of the interesting design here exists to contain
those behaviours in one place. All of them were measured against the live
endpoint rather than inferred from `$metadata`, which is frequently a poor guide
to what this service actually does.

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone <this-repo>
cd Task
dotnet run --project src/TripPin.Console
```

No credentials, keys or local services are needed. The app resolves its own
session against the public endpoint on first request.

Two files make builds reproducible rather than dependent on the machine.
`global.json` pins the SDK to 10.0.302 (rolling forward within the major
version), so every tool, CLI and IDE alike, resolves the same one; if .NET 10 is
missing you get a clear "SDK not found" message instead of confusing downstream
errors. `nuget.config` clears inherited package sources and maps every package
to nuget.org, which matters under Central Package Management: a default Visual
Studio install also registers a local offline-packages folder, and two
unmapped sources make restore non-deterministic.

The menu offers list, search, view and edit. Configuration lives in
`src/TripPin.Console/appsettings.json`: service address, timeouts, page size,
cache TTLs, and retry/circuit-breaker settings. Nothing is hardcoded outside it.

## Architecture

Four projects, dependencies pointing inward only:

```
TripPin.Console  ──┬──>  TripPin.Application  ──>  TripPin.Domain
TripPin.Infrastructure ─┘                              (zero dependencies)
```

| Project | Responsibility |
|---|---|
| `TripPin.Domain` | `Person` aggregate and value objects. No project or package references at all. |
| `TripPin.Application` | Use cases (list, search, detail, update) plus the `IPeopleRepository` port. CQRS-lite, no mediator. |
| `TripPin.Infrastructure` | OData client, session resolution, Polly resilience, caching decorator. Implements the port. |
| `TripPin.Console` | Menus, prompts, formatting. Orchestration and I/O only. |

The decisions worth reading about, and why, are in **[docs/adr/](docs/adr/)**:

- [ADR-001](docs/adr/ADR-001-odata-client-choice.md): typed `HttpClient` over `Microsoft.OData.Client`
- [ADR-002](docs/adr/ADR-002-caching-strategy.md): cache reads behind a decorator, never cache ETags
- [ADR-003](docs/adr/ADR-003-onion-over-flat-layout.md): Onion over a flat layout, and why no MediatR
- [ADR-004](docs/adr/ADR-004-writable-fields-and-addressinfo.md): which fields are writable, and why `AddressInfo` is not

`docs/adr/README.md` also carries a table of every measured service quirk with
the consequence each has for the code.

## Tests

350 unit tests, 15 integration tests. The split matters: the unit suite touches
no network and is safe to run anywhere, while the integration suite writes to a
shared public sandbox.

```bash
# Unit only. Fast (about 12s), no network.
dotnet test --filter "Category!=Integration"

# Integration only. Hits the live service, about 40s.
dotnet test --filter "Category=Integration"

# Everything.
dotnet test
```

Integration tests are gated by a `Category` trait rather than skipped
attributes, so they are opt-in without being invisible. Each builds its own
session (so it starts from pristine data) and calls `ResetDataSource` on
teardown whether it passed or failed, so a failure cannot strand the sandbox for
the next run or for anyone else.

There is no `TripPin.Console.Tests`. Once the menus hold only I/O and
orchestration, there is nothing left worth asserting; the reasoning and the
trigger to revisit it are in ADR-003.

## Known limitations and tradeoffs

**No authentication.** The TripPin sample service is unauthenticated and public.
There is no credential handling, token cache or auth pipeline, because there is
nothing to authenticate against. A real service would need an
`AuthenticationHandler` in the `HttpClient` pipeline (inside the resilience
handler, so a retry re-acquires an expired token) and secrets sourced from the
environment or a vault rather than `appsettings.json`.

**No persistence of our own.** The OData service is the only store. There is no
local database, and the in-memory cache is a short-lived read cache, not a
source of truth. Everything is lost when the process exits, which is correct for
this tool but means it cannot work offline.

**Session-scoped data.** Each run resolves its own session, and each session
starts from a pristine copy of the sample data. Edits made in one run are not
visible in the next. This is the service's behaviour, not ours, and it is why
`SessionUriProvider` resolves exactly once and shares that result.

**Every update costs two round trips.** The service rejects any write without an
`If-Match` header (`428`), so editing is necessarily read-then-write. The read
deliberately bypasses the cache (`GetForUpdateAsync`), because a cached ETag is a
stale ETag and a stale ETag is a rejected write. A `412` is surfaced as "this
record changed since you loaded it, reload and retry?" rather than being retried
silently, since the server state is no longer what the user was looking at.

**Console prompts are not truly cancellable.** A blocked `Console.ReadLine`
cannot be interrupted mid-read. Cancellation tokens exist to abort network
calls, which they do; Ctrl+C unblocks a pending prompt by returning null, which
every screen treats as "leave this screen".

### Service quirks worth knowing about

These are measured, each has a named regression test, and each shaped the code:

**`@odata.count` is computed after `$top`/`$skip`.** Against the specification:
`$count=true` reports 20, but `$count=true&$top=8` reports 8. Reading the total
from a paged response would make every result set look like a single page,
silently hiding most of the data with no error. Listing therefore issues two
requests, one for the total and one for the page. This was caught only by
running the integration suite against the live endpoint.

**The `Gender` enum encoding is inverted between reads and writes.** In
`$filter` the literal must be fully qualified
(`Microsoft.OData.SampleService.Models.TripPin.PersonGender'Female'`); the bare
form returns `500`. In a PATCH body it is exactly the opposite: bare `"Female"`
works and the qualified form returns `500`. This is why `ODataFilterTranslator`
and `PersonMapper` are separate types; a single shared helper would be wrong on
one side by construction.

**A partial `AddressInfo` write corrupts the record irrecoverably.** A PATCH
omitting the required `City` returns `204 No Content`, after which every read of
that person *and of the entire `People` collection* fails with `500`. Recovery
requires a blind write or `ResetDataSource`. `AddressInfo` is therefore
read-only: displayed on the detail screen, never in an update payload, and no
edit affordance is offered anywhere. This is also why updates are sparse by
construction rather than as an optimisation. See ADR-004.

**Other measured behaviours:** `GET` of a missing entity returns `204`, not
`404`. `Emails: null` returns `500` while `Emails: []` clears the collection.
`Gender: null` is accepted with `204` and silently coerced to `Male`. An unknown
field in `$filter` returns `200` with zero rows rather than an error. `PUT`
behaves as a merge, not a replace. A change to the key is accepted and silently
ignored.

### Containerization

Deliberately skipped. This is a single-user, interactive console tool with no
deployment target, no server component and no runtime dependencies beyond the
.NET SDK. A Dockerfile would add a build step and an image to maintain while
making the app harder to run, since an interactive TTY in a container is more
friction than `dotnet run`, not less.

If this became a deployed service, the shape would change first and the
container would follow: the menu loop would be replaced by an HTTP API or worker
host, and I would use a multi-stage `mcr.microsoft.com/dotnet/sdk` build onto
the runtime-only base image, move configuration to environment variables and a
secret store, add a health endpoint for orchestrator probes, and treat
`SessionUriProvider` as the first thing to reconsider, since per-process session
affinity does not survive horizontal scaling.

## AI Engineering Notes

This project was built in a single session using Claude Code, with the model
doing the analysis, architecture and implementation under review at each step.
The work ran roughly in order: fetching and analysing `$metadata` plus probing
the live endpoint to establish real behaviour, agreeing an architecture and
recording it as ADRs, scaffolding the solution, implementing layer by layer
(Domain, Application, Infrastructure, Console), then a coverage review and this
final self-review.

The output was not accepted uncritically, and the most useful moments were the
corrections:

- **The `UpdateAsync` port signature was rejected and redesigned.** The
  scaffolded `UpdateAsync(Person, PersonChanges, ct)` would have forced the
  handler to re-read the person to build its argument, producing a *fresh* ETag
  and silently defeating conflict detection: every concurrent edit would have
  become a lost update rather than a reported `412`. Replaced with a sparse
  `PersonUpdate` carrying the caller's own token.
- **The `@odata.count` quirk was caught by insisting on live integration tests.**
  The first implementation read the total from the paged response, which is
  exactly what the specification says should work. It reported "page 1 of 1" for
  every query. No unit test against a stub would have found it.
- **Writing tests for the resilience pipeline surfaced two real bugs.** The
  pipeline had no tests at all, because every other Infrastructure test bypassed
  Polly. Adding them showed that genuine `HttpClient` timeouts were never
  retried (they arrive as `TaskCanceledException` wrapping `TimeoutException`,
  which the predicate did not match) and that `MaxRetryAttempts: 0` crashed at
  startup.

Two smaller ones: a `400` was being reported to users as "the service could not
be reached", and an early draft leaked a raw stack trace to the console because
host startup sat outside the guarded block.

These notes are an index, not a substitute for the record. The full exported
transcript shows the complete sequence, including the prompts, the rejected
approaches and the verification at each step.
