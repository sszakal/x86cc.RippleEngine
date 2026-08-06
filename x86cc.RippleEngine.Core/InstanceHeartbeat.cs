namespace x86cc.RippleEngine.Core;

/// <summary>
/// One row per live engine instance. Each instance updates <see cref="LastSeenAt"/> every few seconds;
/// the table is the cluster membership list. An instance whose heartbeat goes stale past a threshold is
/// declared dead, and the recovery loop requeues its in-flight ripples and frees its slots.
/// </summary>
public class InstanceHeartbeat
{
    public string InstanceId { get; set; } = "";

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Ripples this instance is currently executing — a live figure the dashboard aggregates across the cluster.</summary>
    public int Executing { get; set; }
}
