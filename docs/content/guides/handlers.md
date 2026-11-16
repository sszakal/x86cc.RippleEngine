---
title: Writing handlers
sidebar_position: 1
---

# Writing handlers

A developer implements exactly one interface:

```csharp
public interface IRippleHandler<in TWave, in TRipple> where TRipple : IRippleTarget
{
    Task<SplashReport?> Execute(TWave wave, TRipple ripple, IRippleContext context);
}
```

- `TWave` is the **wave payload** — the shared event, serialized once onto the wave row.
- `TRipple` is the **ripple payload** — one task, implementing `IRippleTarget` so it declares the target ids
  it covers (`TargetIds`). A ripple may target one entity or a batch of them.
- `IRippleContext` gives read-only attempt info (`Attempt`, `MaxAttempts`, …), a timeout `CancellationToken`,
  and the wave/ripple ids needed for [in-flight expansion](/guides/expansion). See
  [Handler context & testing](/guides/context-and-testing) for every member and how to unit-test a handler.

Register it in each worker with `AddHandler<TWave, TRipple, THandler>(batchSize, gapSeconds, maxAttempts)`.
The handler is resolved from a **per-ripple DI scope**. The parameterless `AddHandler<TWave, TRipple, THandler>()`
registers the handler without seeding a type row, so the type inherits the reserved `__default__` schedule
(see [Configuration](/guides/configuration#per-type-scheduling)). A worker only claims ripples whose `type_key`
it has a handler for. Unhandled ripples stay `Pending` until an instance that can run them picks them up.

## Reporting is by exception

Return `null` (or an empty report) and every target counts as succeeded. Build a `SplashReport` only to
annotate the ones that deviate:

```csharp
var report = SplashReport.Create();
report.Failed(badId, "no ledger for period");     // any Failed target ⇒ the attempt fails and retries
report.Success(otherId, "recomputed from draft"); // succeeded, with a note kept in the audit trail
return report;
```

| Handler does | Result |
|---|---|
| returns `null` / empty report | every target succeeded |
| reports `Success(id, note)` | succeeded, note kept in the audit trail |
| reports any `Failed(id, msg)` | **the whole ripple fails** and retries |
| throws | every target fails with the exception message |

The report aggregates targets sharing an `(outcome, message)` into one item, so 200k successes collapse to a
single row. Unmentioned targets are inferred succeeded. `Success` and `Failed` also take an `IEnumerable<string>`
of ids for one shared message (see [Building a SplashReport](/guides/context-and-testing#building-a-splashreport)).

## Retries

A failed attempt retries with exponential backoff (`RetryBackoff`, doubled per attempt, capped by
`MaxRetryBackoff`) up to the type's `max_attempts`, then goes terminally `Failed`. `max_attempts` is per type
(from `AddHandler`, editable at runtime on the dashboard), falling back to the engine default of 5.

A backing-off ripple keeps its original `schedule_order`, so once eligible it re-claims at its original queue
position.

## Timeouts and cancellation

- `ExecutionTimeout` (default 5 min) cancels `ctx.CancellationToken`. Honour it.
- A handler still running `HandlerCancellationGrace` (default 5s) after its token was cancelled is
  **abandoned**: the attempt is recorded Failed and the slot freed. The handler itself cannot be killed and may
  run on.
- A handler cancelled because the **shutdown drain** ran out is *released*, not failed: requeued at once with
  the attempt handed back, so a restart never spends a ripple's retries.

## Handlers must be idempotent

A ripple can run more than once — via retry, via crash recovery of a dead instance, or after an abandoned
attempt. Write handlers so re-running them is harmless. The one thing a handler *cannot* make idempotent
itself — `Continue(context)` — the engine does for you (see [In-flight expansion](/guides/expansion)).

:::warning Marten handlers
Open your **own** session from `IDocumentStore` (`store.LightweightSession()`), not an injected scoped
`IDocumentSession` — the latter doesn't isolate cleanly across the engine's per-ripple scopes under
concurrency.
:::

## Type names

A payload type's persisted name (its `payload_type`, and each half of the `type_key`) comes from
`RippleTypeName.Of<T>()` — `[RippleType("…")]` if present, else the CLR name. **Pin a name with the attribute
before renaming a payload class**, or ripples already in the database lose their handler:

```csharp
[RippleType("RecalcCompany")]
public sealed class RecalculateCompanyTax : IRippleTarget { /* … */ }
```
