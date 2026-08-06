using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>Shared wave payload (the "event"): what changed, seen by every ripple in the wave.</summary>
public sealed class RecalcContext
{
    public string LegislationCode { get; set; } = "";
}

/// <summary>Per-ripple payload (the target): which company to recalculate.</summary>
public sealed class RecalcCompany : IRippleTarget
{
    public Guid CompanyId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}

/// <summary>
/// A singleton the engine test inspects to see what actually ran. Records every successful execution and
/// how many attempts each ripple took, and lets a test inject failures via <see cref="OnExecute"/>.
/// </summary>
public sealed class ExecutionSink
{
    public ConcurrentBag<Guid> Executed { get; } = [];

    public ConcurrentDictionary<Guid, int> Attempts { get; } = new();

    /// <summary>Optional hook run before a ripple is marked executed — throw from it to simulate failures.</summary>
    public Func<RecalcCompany, int, Task>? OnExecute { get; set; }

    /// <summary>
    /// Like <see cref="OnExecute"/>, but also handed the ripple's context so a test can observe
    /// <see cref="IRippleContext.CancellationToken"/> — needed to prove that shutdown DRAINS in-flight handlers
    /// rather than cancelling them out from under their attempt.
    /// </summary>
    public Func<RecalcCompany, int, IRippleContext, Task>? OnExecuteWithContext { get; set; }

    /// <summary>Set by a handler that observed its context token cancel.</summary>
    public int CancelledCount;
}

/// <summary>
/// Wraps a real splash store and throws on its first <see cref="CompleteRipplesAsync"/> call, to prove the
/// pipeline retries a failed settlement instead of dropping the outcomes (which would leak the ripples).
/// </summary>
public sealed class FlakySplashStore(ISplashStore inner) : ISplashStore
{
    private int _completeCalls;

    public Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId,
        CancellationToken ct = default)
        => Interlocked.Increment(ref _completeCalls) == 1
            ? throw new InvalidOperationException("transient settlement failure")
            : inner.CompleteRipplesAsync(batch, instanceId, ct);

    public Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId,
        CancellationToken ct = default)
        => inner.FailRipplesAsync(batch, instanceId, ct);
}

/// <summary>
/// A splash store whose writes ALWAYS fail — so an outcome can never be settled. Used to reproduce genuine
/// shutdown stranding: the ripple executed, but its row is still <c>Running</c> when the process stops.
/// </summary>
public sealed class AlwaysFailingSplashStore : ISplashStore
{
    public Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId,
        CancellationToken ct = default)
        => throw new InvalidOperationException("settlement is down");

    public Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId,
        CancellationToken ct = default)
        => throw new InvalidOperationException("settlement is down");
}

/// <summary>The one interface a developer writes: recalculate one company's tax under the new legislation.</summary>
public sealed class RecalcHandler(ExecutionSink sink) : IRippleHandler<RecalcContext, RecalcCompany>
{
    public async Task<SplashReport?> Execute(RecalcContext wave, RecalcCompany ripple, IRippleContext context)
    {
        var attempt = sink.Attempts.AddOrUpdate(ripple.CompanyId, 1, (_, v) => v + 1);

        if (sink.OnExecute is { } hook)
        {
            await hook(ripple, attempt); // throwing here fails the ripple (→ retry)
        }

        if (sink.OnExecuteWithContext is { } contextHook)
        {
            try
            {
                await contextHook(ripple, attempt, context);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref sink.CancelledCount);
                throw;
            }
        }

        sink.Executed.Add(ripple.CompanyId);
        return null; // inferred succeeded
    }
}

// ---- Heterogeneous fan-out + expansion showcase -------------------------------------------------
//
// A single "legislation changed" wave (RecalcContext) targets three DIFFERENT kinds of entity — three
// distinct ripple types, hence three type_keys, three handlers, each independently scheduled. One of them,
// the company group, is not taxed directly: its handler EXPANDS the wave, spawning one CompanyTax ripple per
// member company (children in the same wave, parented to the group ripple).

/// <summary>Per-ripple target: a sole trader to re-assess.</summary>
public sealed class SoleTraderTax : IRippleTarget
{
    public Guid TraderId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [TraderId.ToString()];
}

/// <summary>Per-ripple target: a single company to re-assess (also the type the group ripple expands into).</summary>
public sealed class CompanyTax : IRippleTarget
{
    public Guid CompanyId { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [CompanyId.ToString()];
}

/// <summary>Per-ripple target: a company group, which is made up of member companies (the group is the target).</summary>
public sealed class CompanyGroupTax : IRippleTarget
{
    public Guid GroupId { get; set; }
    public List<Guid> MemberCompanyIds { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => [GroupId.ToString()];
}

/// <summary>
/// A minimal <see cref="IRippleContext"/> for storage-level tests that exercise <c>Continue(context)</c>
/// without running the engine — only <see cref="WaveId"/>/<see cref="RippleId"/> are read by the generators.
/// </summary>
public sealed class StubRippleContext(Guid waveId, Guid rippleId) : IRippleContext
{
    public Guid WaveId { get; } = waveId;
    public Guid RippleId { get; } = rippleId;
    public int Attempt => 1;
    public int MaxAttempts => 1;
    public int CurrentRetryCount => 0;
    public int RetryCount => 0;
    public CancellationToken CancellationToken => CancellationToken.None;
}

// ---- Per-target report inference showcase -------------------------------------------------------

/// <summary>A batched ripple: many targets in one ripple, so the handler can report per-target outcomes.</summary>
public sealed class BatchTax : IRippleTarget
{
    public string[] Ids { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> TargetIds => Ids;
}

/// <summary>Drives <see cref="ReportingHandler"/>: what it returns (or throws) for a given batch.</summary>
public sealed class ReportSink
{
    public Func<BatchTax, SplashReport?>? OnExecute { get; set; }
    public ConcurrentBag<int> AttemptCounts { get; } = [];
    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);

    internal void Bump() => Interlocked.Increment(ref _attempts);
}

/// <summary>Returns whatever <see cref="ReportSink.OnExecute"/> produces (or throws), to exercise inference.</summary>
public sealed class ReportingHandler(ReportSink sink) : IRippleHandler<RecalcContext, BatchTax>
{
    public Task<SplashReport?> Execute(RecalcContext wave, BatchTax ripple, IRippleContext context)
    {
        sink.Bump();
        return Task.FromResult(sink.OnExecute?.Invoke(ripple)); // OnExecute may throw to simulate a crash
    }
}

/// <summary>Records what actually ran, per ripple type, for the heterogeneous-fan-out test to assert against.</summary>
public sealed class HierarchySink
{
    public ConcurrentBag<Guid> SoleTraders { get; } = [];
    public ConcurrentBag<Guid> Companies { get; } = [];
    public ConcurrentBag<Guid> Groups { get; } = [];
}

public sealed class SoleTraderTaxHandler(HierarchySink sink) : IRippleHandler<RecalcContext, SoleTraderTax>
{
    public Task<SplashReport?> Execute(RecalcContext wave, SoleTraderTax ripple, IRippleContext context)
    {
        sink.SoleTraders.Add(ripple.TraderId);
        return Task.FromResult<SplashReport?>(null);
    }
}

public sealed class CompanyTaxHandler(HierarchySink sink) : IRippleHandler<RecalcContext, CompanyTax>
{
    public Task<SplashReport?> Execute(RecalcContext wave, CompanyTax ripple, IRippleContext context)
    {
        sink.Companies.Add(ripple.CompanyId);
        return Task.FromResult<SplashReport?>(null);
    }
}

/// <summary>
/// A company group isn't taxed directly — it expands into one <see cref="CompanyTax"/> ripple per member,
/// which the <see cref="CompanyTaxHandler"/> then processes. Shows in-memory in-flight expansion: the members
/// are already in the payload, so it uses the source-less <see cref="ICollectionWaveGenerator.Continue"/> —
/// the same <c>Continue(context)</c> verb the queryable generators expose (see
/// <see cref="MartenGroupExpandHandler"/> for the server-side counterpart).
/// </summary>
public sealed class CompanyGroupTaxHandler(ICollectionWaveGenerator generator, HierarchySink sink)
    : IRippleHandler<RecalcContext, CompanyGroupTax>
{
    public async Task<SplashReport?> Execute(RecalcContext wave, CompanyGroupTax ripple, IRippleContext context)
    {
        var children = ripple.MemberCompanyIds
            .Select(id => new CompanyTax { CompanyId = id })
            .ToList();
        await generator.Continue(context).AddRipples(children).DispatchAsync(context.CancellationToken);

        sink.Groups.Add(ripple.GroupId);
        return null; // the group target is inferred succeeded once its members are enqueued
    }
}
