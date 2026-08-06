namespace x86cc.RippleEngine.Storage;

/// <summary>The outcome of resetting a type's <c>type_schedule</c> row.</summary>
public enum TypeScheduleDeleteResult
{
    /// <summary>The row was removed; the type now re-inherits the DEFAULT row.</summary>
    Deleted,

    /// <summary>No row for that type (it was already inheriting), or the reserved DEFAULT key was passed.</summary>
    NotFound,

    /// <summary>
    /// Refused: the type is <c>'paused'</c> or <c>'resuming_*'</c>. The row holds the pause state machine, so
    /// deleting it while ripples sit in <c>'Paused'</c> would leave them unclaimable forever — the reconcile
    /// only revisits types whose <c>pause_state</c> says there is work to move. Resume the type first.
    /// </summary>
    PauseInProgress
}
