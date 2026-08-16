// Vocabulary mirrors the engine: Wave = job (a fan-out). The dashboard shows waves and per-type metrics;
// individual ripples/splashes are intentionally not browsable (they're reclaimed at compaction).

export type WaveStatus = 'Active' | 'Completed' | 'Faulted';

export interface Wave {
  id: string;
  name: string;
  type: string | null;
  status: WaveStatus;
  payloadType?: string | null;
  rippleCount: number;
  pending: number;
  running: number;
  /**
   * Ripples parked because their type is paused. Not pending, not done — without it a paused wave reads as
   * 0 pending / 0 running with an unfinished progress bar and nothing explaining the gap. `succeeded` is
   * derived server-side net of it.
   */
  paused: number;
  succeeded: number;
  failed: number;
  /** Re-execution attempts (splashes with attempt > 1). Recomputed live while active; final at compaction. */
  retryCount: number;
  /** Mean per-attempt execution time (ms) over succeeded splashes — computed at compaction; null until then. */
  avgDurationMs: number | null;
  splashSampleCount: number;
  /** Wall-clock time (ms) from wave creation to completion; null while still active. */
  durationMs: number | null;
  /** Achieved throughput (ripples/sec) over the wave's wall-clock duration; null while still active. */
  throughput: number | null;
  createdAt: string;
  completedAt: string | null;
  compactedAt: string | null;
}

export interface WaveStats {
  total: number;
  active: number;
  completed: number;
  faulted: number;
}

export interface WavesResponse {
  waves: Wave[];
  stats: WaveStats;
}

export interface WaveFilter {
  status?: WaveStatus;
  q?: string;
  from?: string;
  to?: string;
}

/** One day's wave activity — the unit of the contribution heatmap. */
export interface WaveActivityDay {
  date: string; // YYYY-MM-DD
  count: number; // waves created that day
  completed: number;
  faulted: number;
  running: number; // still Active
}

export interface WaveActivityResponse {
  days: WaveActivityDay[];
}

/** One time bucket at some granularity (hour/minute/second) — the unit of the adaptive tile-zoom. */
export interface HistogramBucket {
  start: string; // ISO timestamp of the bucket's start
  count: number;
  completed: number;
  faulted: number;
  running: number;
}

export interface HistogramResponse {
  buckets: HistogramBucket[];
}

/** Per-type throughput + average execution time (the Metrics page). */
export interface TypeMetric {
  typeKey: string;
  batchSize: number | null;
  gapSeconds: number | null;
  /** Steady-state schedule share ≈ batchSize / gapSeconds. */
  share: number | null;
  sampleCount: number;
  /** Count-weighted EWMA of execution time (ms) across compacted waves of this type; null until some compact. */
  avgMs: number | null;
  /** Count-weighted EWMA of queue wait (ms: creation → claim); null until some compact. */
  avgWaitMs: number | null;
  /** Per-wave EWMA of the retry rate (retries ÷ ripples) across compacted waves; a fraction, null until some compact. */
  avgRetryRate: number | null;
}

/** The scheduler's default config (the reserved '__default__' row every unconfigured type inherits). */
export interface DefaultSetting {
  batchSize: number;
  gapSeconds: number;
  maxAttempts: number;
}

/** One registered (wave|ripple) type's scheduling config. When `configured` is false it has no row of its own
 *  and inherits the default; the `seeded*` fields are the hard-coded AddHandler(...) value, shown as a hint. */
/** The pause state machine for a type: the desired state a background loop reconciles the ripples toward. */
export type PauseState = 'active' | 'paused' | 'resuming_rebase' | 'resuming_asis';

export interface TypeSetting {
  typeKey: string;
  configured: boolean;
  /** Desired pause state: 'paused' skips the type at claim time (its work parks async); 'resuming_*' is draining back. */
  pauseState: PauseState;
  batchSize: number | null;
  gapSeconds: number | null;
  maxAttempts: number | null;
  seededBatchSize: number | null;
  seededGapSeconds: number | null;
  seededMaxAttempts: number | null;
}

export interface TypeSettingsResponse {
  default: DefaultSetting | null;
  types: TypeSetting[];
}

/** The editable payload for a type's config; maxAttempts null ⇒ inherit the default row's ceiling. */
export interface TypeScheduleUpdate {
  batchSize: number;
  gapSeconds: number;
  maxAttempts: number | null;
}

/** Read-only engine options for one worker instance (per-instance / env-tunable, not DB-stored). */
export interface EngineInfo {
  instanceId: string;
  maxConcurrency: number;
  prefetchFactor: number;
  claimBatchSize: number;
  executionTimeoutSeconds: number;
  heartbeatTimeoutSeconds: number;
  waveStatsRefreshSeconds: number;
  compactionSeconds: number;
  reportChunkSize: number;
  defaultRetentionDays: number | null;
  retentionByWaveType: Record<string, number | null>;
}

/** One live engine instance, from its heartbeat. */
export interface ClusterInstance {
  instanceId: string;
  lastSeenAt: string;
  executing: number;
  ageSeconds: number;
}

export interface ClusterResponse {
  instances: ClusterInstance[];
}
