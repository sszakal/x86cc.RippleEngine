namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The composite discriminator that drives both scheduling config and handler resolution:
/// <c>"{waveType}|{rippleType}"</c>. Built from the wave's payload type and the ripple's payload type, it is
/// the single key for <c>type_schedule</c> (the batch/gap knobs) and the handler registry. A null part renders
/// as empty (e.g. an untyped wave yields <c>"|SomeRipple"</c>), which falls back to the engine's default
/// batch/gap.
/// </summary>
public static class RippleTypeKey
{
    public const char Separator = '|';

    /// <summary>
    /// The reserved <c>type_key</c> of the <b>default config row</b> in <c>type_schedule</c> — the single row
    /// (seeded by the migration) that holds the fall-back batch/gap/max_attempts every unconfigured type
    /// inherits. The hot-path SQL reads a type's own row and <c>coalesce</c>s to this row, so the scheduler's
    /// defaults live in the database (editable from the dashboard), not as inlined constants. It contains no
    /// <see cref="Separator"/>, so it can never collide with a composed <c>"{waveType}|{rippleType}"</c> key.
    /// </summary>
    public const string Default = "__default__";

    public static string Compose(string? waveType, string? rippleType) => $"{waveType}{Separator}{rippleType}";

    /// <summary>
    /// Whether <paramref name="typeKey"/> can name a <c>type_schedule</c> row: <see cref="Default"/>, or a
    /// <see cref="Compose"/>d key — exactly one <see cref="Separator"/> (either half may be empty, as
    /// <see cref="Compose"/> allows; a persisted payload name never contains the separator). Lets an edit path
    /// refuse a typo such as <c>"RecalcContext"</c> instead of writing a row no ripple will ever match.
    /// </summary>
    public static bool IsWellFormed(string? typeKey)
        => typeKey == Default
           || (typeKey is not null && typeKey.Count(c => c == Separator) == 1);
}
