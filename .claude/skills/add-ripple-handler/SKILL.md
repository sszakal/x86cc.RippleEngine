---
name: add-ripple-handler
description: Playbook for adding a new wave/ripple type to RippleEngine (or the sample) — payload types, the IRippleHandler, AddHandler registration with batch/gap/maxAttempts, the fan-out call site, and tests. Use when introducing a new job type, a new handler, or a new (wave, ripple) pair.
---

# Adding a wave/ripple type

A new job type is always: **payloads → handler → `AddHandler` registration → fan-out call site → tests.**
The composite `type_key = "{waveType}|{rippleType}"` ties them together; it is both the handler key and the
`type_schedule` config key, so every step must resolve the same names.

## 1. Payloads

```csharp
[RippleType("VatChange")]                 // pin the persisted name before it ships
public sealed class VatChange { public string Code { get; set; } = ""; public decimal Rate { get; set; } }

[RippleType("RecalcVat")]
public sealed class RecalcVat : IRippleTarget
{
    public Guid CompanyId { get; set; }
    [JsonIgnore] public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}
```

- The ripple payload **must** implement `IRippleTarget`; `TargetIds` is a computed projection — `[JsonIgnore]` it.
- Names come only from `RippleTypeName.Of<T>()` (`[RippleType]` or the CLR name). Never stamp `typeof(T).Name`.
  Renaming an unpinned class orphans ripples already in the DB.
- Enums inside payloads must be stored as strings (the Marten fan-out writes enum literals into JSONB).
- Payloads are (de)serialized with `RippleJson.Options` only.

## 2. Handler

```csharp
public sealed class RecalcVatHandler(IDocumentStore store) : IRippleHandler<VatChange, RecalcVat>
{
    public async Task<SplashReport?> Execute(VatChange wave, RecalcVat ripple, IRippleContext context)
    {
        await using var session = store.LightweightSession();   // own session, not an injected IDocumentSession
        ...
        return null;   // null ⇒ every target succeeded
    }
}
```

- **Idempotent** — a ripple can run more than once (retry, recovery, release on shutdown).
- Report by exception: `new SplashReport().Failed(id, msg)` only for deviating targets; throwing fails all
  targets; any `Failed` target retries the whole ripple.
- Honour `context.CancellationToken`; a handler that ignores it past `HandlerCancellationGrace` is abandoned.
- To expand the wave in flight use the generator's `Continue(context)` (pass an explicit `expansionKey` if the
  expansion order can vary between attempts) — the engine makes each expansion exactly-once.

## 3. Register

```csharp
builder.AddRippleEngine(o => { ... o.Retention<VatChange>(TimeSpan.FromDays(90)); })
       .AddHandler<VatChange, RecalcVat, RecalcVatHandler>(batchSize: 10, gapSeconds: 1, maxAttempts: 5);
```

- Throughput share ≈ `batchSize / gapSeconds` relative to competing types. Batch/gap are baked into
  `schedule_order` at fan-out (changing them later affects only new fan-outs, or an explicit reschedule);
  `maxAttempts` is read at claim time.
- Only instances that registered the handler claim that `type_key`; creation-only hosts
  (`EnableWorkers = false`) don't need the handler but do need the payload types.

## 4. Fan-out call site

Pick the source: `ICollectionWaveGenerator` (in-memory), `IMartenWaveGenerator` (`Create(session, …)`), or
`IEfWaveGenerator` (`Create(dbContext, …)`); then `.AddRipples<TSource, TRipple>(filter, projection)` and
`.DispatchAsync()`. Use `.WithIdempotencyKey(key)` when the caller may retry the create.

## 5. Tests

- Add an engine/storage test deriving from `RippleTestBase` (see `TestExecutors.cs` for handler/payload
  fixtures); call `RefreshWaveStatsAsync()` before asserting wave numbers.
- Cover: happy path, a `Failed` target retrying to terminal, and — if the handler expands — the retry
  inserting nothing twice.
- Run via the `run-tests` skill.

## 6. Docs

If this changes public behaviour or adds a documented pattern, update `docs/content/guides/handlers.md` /
`fan-out.md` / `configuration.md` (and `docs/sidebars.ts` for a new page).
