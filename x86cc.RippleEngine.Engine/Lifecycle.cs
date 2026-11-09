namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The one-shot start → stop lifecycle of an engine component (<see cref="ExecutionPipeline"/>,
/// <see cref="SettlementWriter"/>). Misuse throws a clear <see cref="InvalidOperationException"/> instead of failing
/// later as a <see cref="NullReferenceException"/> or, worse, silently: a second start would duplicate the
/// component's workers, and a stop without a start would return as if everything had been settled.
/// </summary>
/// <remarks>A mutable struct held in a field — call it on the field directly, never through a copy.</remarks>
internal struct Lifecycle
{
    private const int New = 0, Started = 1, Stopped = 2;

    private int _state;

    public void Start(string component)
    {
        if (Interlocked.CompareExchange(ref _state, Started, New) != New)
        {
            throw new InvalidOperationException($"{component} has already been started; it can be started once.");
        }
    }

    public void Stop(string component)
    {
        switch (Interlocked.CompareExchange(ref _state, Stopped, Started))
        {
            case New:
                throw NotStarted(component, "stop");
            case Stopped:
                throw new InvalidOperationException($"{component} has already been stopped.");
        }
    }

    public static InvalidOperationException NotStarted(string component, string action)
        => new($"{component} must be started before it can {action}.");
}
