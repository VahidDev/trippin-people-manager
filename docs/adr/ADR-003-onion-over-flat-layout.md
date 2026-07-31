# ADR-003: Onion over a flat layout

**Status:** Accepted
**Date:** 2026-07-31

## Context

A console app against one entity set could plausibly be a single project. Four projects plus three test projects is more ceremony than the feature count alone justifies.

## Decision

Onion, with dependencies pointing inward only: Console → Infrastructure → Application → Domain.

## Rationale

The justification is not size, it is that this service's quirks are unusually invasive. Session-scoped URLs, mandatory ETags, `204`-as-not-found, silent-empty filters, and an open type that swallows typos are all things that, in a flat layout, smear across the codebase and get re-solved inconsistently. A port boundary forces them to be normalised in exactly one place.

The concrete payoff: `IPeopleRepository` lets us test all four use cases against a fake, and test quirk handling against a stubbed `HttpMessageHandler`, with no network in either case, which matters when the live service resets state per session and is shared with the public.

It also keeps the Domain honest about something the service is not: the metadata marks `FirstName` and `LastName` non-nullable but does not enforce it, and open types accept arbitrary misspelled properties. A dependency-free Domain is where those invariants get enforced for real.

## Consequences

More projects, more wiring, one more indirection to trace when reading code. We accept that, but deliberately cap it: no repository-per-entity generalisation, no generic `IRepository<T>`, no MediatR, and no test project for the console.

## On MediatR specifically

Not adopted. Four use cases do not need runtime dispatch, and the cross-cutting concerns it usually justifies (logging, caching, validation) are handled here by DI decorators, which are compile-time-checked and easier to follow than a behaviour pipeline. Two hand-rolled interfaces cost roughly twenty lines.

There is also a licensing consideration: recent MediatR versions moved to a commercial license for larger organisations, which is worth confirming against your situation before taking the dependency anywhere.

If the use case count grows past roughly fifteen, or genuinely order-sensitive pipeline behaviours appear, revisit it.

## Console test project

There is no `TripPin.Console.Tests`. Once the menus hold only I/O and orchestration, there is nothing left worth asserting.

One agreed trigger to revisit: if `OperationScope`'s cancellation and supersede logic grows any more complex, extract it and test it rather than leaving it embedded in a menu class.
