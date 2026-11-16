---
title: Handler context & testing
sidebar_position: 2
---

# Handler context & testing

## `IRippleContext`

Every `Execute` call receives an `IRippleContext`, a read-only handle for the current attempt:

| Member | Meaning |
|---|---|
| `WaveId` | the wave this ripple belongs to |
| `RippleId` | this ripple's id, stamped as `parent_ripple_id` on any children it [expands](/guides/expansion) |
| `Attempt` | this attempt's number, **1-based** (the first splash is attempt 1) |
| `MaxAttempts` | the type's ceiling before a failure becomes terminal (`max_attempts`, else the engine default) |
| `CurrentRetryCount` | retries already made before this attempt (`Attempt - 1`) |
| `RetryCount` | the maximum number of retries allowed (`MaxAttempts - 1`) |
| `CancellationToken` | cancels when `ExecutionTimeout` elapses or the host shuts down |
| `NextExpansionOrdinal()` | used by the wave generators, **not** by handlers. See [below](#nextexpansionordinal) |

### Branching on the last attempt

`Attempt == MaxAttempts` means this is the attempt whose failure is terminal. Use it to degrade gracefully
instead of failing outright, or to record a richer message for the audit trail:

```csharp
public async Task<SplashReport?> Execute(TaxChange wave, RecalcCompany ripple, IRippleContext ctx)
{
    try
    {
        await _ledger.RecalculateAsync(ripple.CompanyId, wave.Rate, ctx.CancellationToken);
        return null;
    }
    catch (LedgerUnavailableException) when (ctx.Attempt < ctx.MaxAttempts)
    {
        throw;                                                    // let the engine back off and retry
    }
    catch (LedgerUnavailableException e)
    {
        return SplashReport.Create()                              // last attempt: fail with a clear reason
            .Failed(ripple.CompanyId.ToString(), $"ledger unavailable after {ctx.Attempt} attempts: {e.Message}");
    }
}
```

Always pass `ctx.CancellationToken` to your I/O. A handler that ignores it is
[abandoned](/guides/handlers#timeouts-and-cancellation) after `HandlerCancellationGrace`. Its slot is freed,
but the code keeps running in the background.

## Building a `SplashReport`

Besides `Success(id, note)` and `Failed(id, reason)`, both verbs have overloads that take many ids sharing one
message. Use them when a handler processes a [batched ripple](/guides/fan-out#batched-fan-out):

```csharp
var report = SplashReport.Create();
report.Failed(missing.Select(id => id.ToString()), "no ledger for period");
report.Success(drafts.Select(id => id.ToString()), "recomputed from draft");
return report;
```

| Member | Use |
|---|---|
| `Success(string id, string? output)` / `Success(IEnumerable<string> ids, string? output)` | mark targets succeeded, with an optional note |
| `Failed(string id, string? output)` / `Failed(IEnumerable<string> ids, string? output)` | mark targets failed. **Any** failure fails the whole attempt |
| `HasFailures` | `true` if any target is `Failed`, meaning the attempt will fail and retry |
| `Items` | the aggregated entries (`Outcome`, `Output`, `TargetIds`), one per distinct `(outcome, output)` |

Splashes have a third outcome, **`SplashOutcome.Abandoned`**. A handler never reports it. Recovery writes it
when an attempt was interrupted because its owning instance died, so a splash with no result still has an
explanation.

## Unit-testing a handler

A handler is plain code over three inputs, and `IRippleContext` is an interface, so you can test one without
Postgres or an engine host. Fake the context:

```csharp
public sealed class FakeRippleContext(int attempt = 1, int maxAttempts = 5) : IRippleContext
{
    private int _expansions;

    public Guid WaveId { get; init; } = Guid.NewGuid();
    public Guid RippleId { get; init; } = Guid.NewGuid();
    public int Attempt => attempt;
    public int MaxAttempts => maxAttempts;
    public int CurrentRetryCount => attempt - 1;
    public int RetryCount => maxAttempts - 1;
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
    public int NextExpansionOrdinal() => Interlocked.Increment(ref _expansions);
}
```

Then call `Execute` directly and assert on the report:

```csharp
[Fact]
public async Task last_attempt_reports_failure_instead_of_throwing()
{
    var handler = new RecalcHandler(new AlwaysDownLedger());

    var report = await handler.Execute(
        new TaxChange { Rate = 0.23m },
        new RecalcCompany { CompanyId = companyId },
        new FakeRippleContext(attempt: 5, maxAttempts: 5));

    report.ShouldNotBeNull();
    report.HasFailures.ShouldBeTrue();
    report.Items.Single().TargetIds.ShouldBe([companyId.ToString()]);
}
```

What this does **not** cover is the SQL: fan-out predicates and projections, and `Continue(context)`
expansions, run against Postgres. Test those against a real database, as the engine's own suite does (see
[Contributing](/contributing#tests)).

### `NextExpansionOrdinal`

Each `Continue(context)` asks the context for the next ordinal. The ordinal identifies the expansion when the
ripple is retried, so the expansion is made once and not once per attempt. A fake only needs to return an
incrementing counter, as above. Handlers never call it.
