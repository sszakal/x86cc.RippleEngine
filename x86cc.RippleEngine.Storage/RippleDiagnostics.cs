using System.Diagnostics;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The engine's tracing source. Inert unless something listens (a host adds it with
/// <c>AddSource(<see cref="ActivitySourceName"/>)</c>, or <c>RippleSetupOptions.EnableTracing</c> does it).
/// </summary>
/// <remarks>
/// Three spans:
/// <list type="bullet">
/// <item><c>ripple.wave.create</c> (producer) — a generator's <c>Create(…).DispatchAsync()</c>: the fan-out.</item>
/// <item><c>ripple.wave.expand</c> — a handler's <c>Continue(context).DispatchAsync()</c>, a child of the
/// execution that made it.</item>
/// <item><c>ripple.execute</c> (consumer) — one attempt of one ripple, on whichever worker claimed it.</item>
/// </list>
/// An execution is <b>linked</b> to the trace that created its wave, not parented under it. One event fans out
/// into up to millions of executions spread over hours: as children they would be a single trace no backend can
/// hold or show, sampled all-or-nothing. As linked root traces each execution is sampled on its own and still
/// points back at the request that caused it. The link target is the wave's <c>trace_parent</c>, captured when
/// the wave is created and carried to the worker on the claim.
/// </remarks>
public static class RippleDiagnostics
{
    public const string ActivitySourceName = "x86cc.RippleEngine";

    /// <summary>The source the engine's spans are started on.</summary>
    public static ActivitySource Source { get; } = new(ActivitySourceName);

    /// <summary>
    /// The W3C <c>traceparent</c> to stamp on a wave being created: the engine's own create span when it is being
    /// recorded, else whatever the caller is running under (its HTTP request span, say) — so executions link back
    /// to the cause even on a host that listens to its own spans but not the engine's. Null when there is no
    /// ambient trace at all.
    /// </summary>
    internal static string? TraceParent(Activity? created)
        => (created ?? Activity.Current) is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;
}
