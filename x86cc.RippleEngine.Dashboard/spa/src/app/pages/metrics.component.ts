import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { formatDuration } from '../core/duration';
import { RippleApiService } from '../core/ripple-api.service';
import { ClusterInstance, QueueSnapshot, TypeMetric } from '../core/models';

// A heartbeat older than this reads as a down/stale replica (heartbeats are frequent).
const STALE_SECONDS = 30;

// The reserved fallback schedule (RippleTypeKey.Default) — a config default, not a real wave type; hide it.
const DEFAULT_TYPE_KEY = '__default__';

@Component({
  selector: 'app-metrics',
  standalone: true,
  imports: [DecimalPipe],
  template: `
    <div class="mb-6">
      <h1 class="text-2xl font-semibold text-gray-900">Metrics</h1>
      <p class="mt-1 text-sm text-gray-500">Live cluster, queue and drain estimate, per-type scheduling share, execution time and queue wait.</p>
    </div>

    <!-- Live cluster -->
    <div class="card mb-6">
      <div class="mb-4 flex items-center justify-between">
        <div class="text-sm font-medium text-gray-700">Cluster</div>
        <div class="text-sm text-gray-500">
          <span class="font-semibold text-emerald-600">{{ liveCount() }}</span> live ·
          <span class="font-semibold text-brand-600">{{ totalExecuting() }}</span> in-flight
        </div>
      </div>
      <div class="space-y-2">
        @for (i of instances(); track i.instanceId) {
          <div class="flex items-center gap-3">
            <span class="h-2 w-2 shrink-0 rounded-full" [class]="isStale(i) ? 'bg-gray-300' : 'bg-emerald-500'"></span>
            <span class="w-72 shrink-0 truncate font-mono text-xs text-gray-600" [title]="i.instanceId">{{ i.instanceId }}</span>
            <div class="h-3 flex-1 overflow-hidden rounded-full bg-gray-100">
              <div class="h-full rounded-full bg-brand-500" [style.width.%]="execPct(i)"></div>
            </div>
            <span class="w-28 shrink-0 text-right text-xs text-gray-700">
              {{ i.executing }} exec
              @if (isStale(i)) { <span class="text-gray-400">· stale {{ i.ageSeconds }}s</span> }
            </span>
          </div>
        } @empty {
          <div class="text-sm text-gray-400">No workers have reported a heartbeat yet.</div>
        }
      </div>
    </div>

    <!-- Queue (cluster-wide): depth, the measured drain rate, and how long the backlog takes at that rate -->
    <div class="card mb-6">
      <div class="mb-4 flex items-center justify-between">
        <div class="text-sm font-medium text-gray-700">Queue</div>
        <div class="text-xs text-gray-400">updates every {{ queueRefreshSeconds }}s</div>
      </div>
      <div class="grid grid-cols-3 gap-4">
        <div>
          <div class="text-xs text-gray-500">Depth</div>
          <div class="text-2xl font-semibold text-gray-900">
            @if (queue(); as q) { {{ q.depth | number }} } @else { — }
          </div>
          <div class="text-xs text-gray-400">pending ripples across active waves</div>
        </div>
        <div>
          <div class="text-xs text-gray-500">Drain rate</div>
          <div class="text-2xl font-semibold text-gray-900">
            @if (queue()?.drainRate; as rate) { {{ rate | number: '1.0-0' }} /s } @else { — }
          </div>
          <div class="text-xs text-gray-400">ripples settled per second, cluster-wide</div>
        </div>
        <div>
          <div class="text-xs text-gray-500">Estimated drain time</div>
          <div class="text-2xl font-semibold text-gray-900">
            @if (queue()?.estimatedDrainSeconds; as eta) { {{ duration(eta) }} } @else { — }
          </div>
          <div class="text-xs text-gray-400">to clear everything claimable at the current rate</div>
        </div>
      </div>
      <p class="mt-4 text-xs text-gray-400">
        The rate is a ~30s moving average of each active wave's progress, so the estimate follows changes in pace
        within about a minute. It assumes that pace holds and doesn't count paused work or future expansions.
      </p>
    </div>

    <!-- Schedule share = batch_size / gap_seconds — a relative fair-share weight, NOT a wall-clock rate -->
    <div class="card mb-6">
      <div class="mb-4 text-sm font-medium text-gray-700">Schedule share (batch ÷ gap)</div>
      <div class="grid items-center gap-x-3 gap-y-3" style="grid-template-columns: max-content minmax(0, 1fr) max-content">
        @for (m of metrics(); track m.typeKey) {
          <div class="contents">
            <div class="whitespace-nowrap font-mono text-xs text-gray-600">{{ m.typeKey }}</div>
            <div class="h-4 overflow-hidden rounded-full bg-gray-100">
              <div class="h-full rounded-full bg-brand-500" [style.width.%]="pct(m.share, maxShare())"></div>
            </div>
            <div class="whitespace-nowrap text-right text-xs text-gray-700">{{ m.share != null ? (m.share | number: '1.0-2') : '—' }}</div>
          </div>
        } @empty {
          <div class="col-span-full text-sm text-gray-400">No type schedules configured yet.</div>
        }
      </div>
      <p class="mt-4 text-xs text-gray-400">
        A relative fair-share weight — only the ratios matter. Actual wall-clock throughput is higher and shown per wave.
      </p>
    </div>

    <!-- Average execution time per type -->
    <div class="card mb-6">
      <div class="mb-4 text-sm font-medium text-gray-700">Average execution time (ms / ripple — EWMA over compacted waves)</div>
      <div class="grid items-center gap-x-3 gap-y-3" style="grid-template-columns: max-content minmax(0, 1fr) max-content">
        @for (m of metrics(); track m.typeKey) {
          <div class="contents">
            <div class="whitespace-nowrap font-mono text-xs text-gray-600">{{ m.typeKey }}</div>
            <div class="h-4 overflow-hidden rounded-full bg-gray-100">
              <div class="h-full rounded-full bg-emerald-500" [style.width.%]="pct(m.avgMs, maxAvg())"></div>
            </div>
            <div class="whitespace-nowrap text-right text-xs text-gray-700">
              @if (m.avgMs != null) { {{ m.avgMs }} ms <span class="text-gray-400">({{ m.sampleCount }})</span> }
              @else { <span class="text-gray-400">no data yet</span> }
            </div>
          </div>
        } @empty {
          <div class="col-span-full text-sm text-gray-400">No metrics yet — run and compact some waves.</div>
        }
      </div>
    </div>

    <!-- Average queue wait per type (creation → claim: backpressure signal) -->
    <div class="card mb-6">
      <div class="mb-4 text-sm font-medium text-gray-700">Average queue wait (ms: created → claimed — EWMA)</div>
      <div class="grid items-center gap-x-3 gap-y-3" style="grid-template-columns: max-content minmax(0, 1fr) max-content">
        @for (m of metrics(); track m.typeKey) {
          <div class="contents">
            <div class="whitespace-nowrap font-mono text-xs text-gray-600">{{ m.typeKey }}</div>
            <div class="h-4 overflow-hidden rounded-full bg-gray-100">
              <div class="h-full rounded-full bg-amber-500" [style.width.%]="pct(m.avgWaitMs, maxWait())"></div>
            </div>
            <div class="whitespace-nowrap text-right text-xs text-gray-700">
              @if (m.avgWaitMs != null) { {{ m.avgWaitMs }} ms }
              @else { <span class="text-gray-400">no data yet</span> }
            </div>
          </div>
        } @empty {
          <div class="col-span-full text-sm text-gray-400">No metrics yet — run and compact some waves.</div>
        }
      </div>
      <p class="mt-4 text-xs text-gray-400">
        Execution time and wait are computed at compaction over each wave's succeeded splashes, as a
        count-weighted moving average — a type shows data only after one of its waves has compacted.
      </p>
    </div>

    <!-- Average retry rate per type (retries ÷ ripples per wave: handler health signal) -->
    <div class="card">
      <div class="mb-4 text-sm font-medium text-gray-700">Average retry rate per wave (retries ÷ ripples — EWMA over compacted waves)</div>
      <div class="grid items-center gap-x-3 gap-y-3" style="grid-template-columns: max-content minmax(0, 1fr) max-content">
        @for (m of metrics(); track m.typeKey) {
          <div class="contents">
            <div class="whitespace-nowrap font-mono text-xs text-gray-600">{{ m.typeKey }}</div>
            <div class="h-4 overflow-hidden rounded-full bg-gray-100">
              <div class="h-full rounded-full bg-rose-500" [style.width.%]="pct(m.avgRetryRate, maxRetryRate())"></div>
            </div>
            <div class="whitespace-nowrap text-right text-xs text-gray-700">
              @if (m.avgRetryRate != null) { {{ m.avgRetryRate * 100 | number: '1.0-2' }}% }
              @else { <span class="text-gray-400">no data yet</span> }
            </div>
          </div>
        } @empty {
          <div class="col-span-full text-sm text-gray-400">No metrics yet — run and compact some waves.</div>
        }
      </div>
      <p class="mt-4 text-xs text-gray-400">
        A per-wave moving average of how often ripples had to be re-executed, size-normalised so waves of any
        scale compare — a type shows data only after one of its waves has compacted.
      </p>
    </div>
  `
})
export class MetricsComponent implements OnInit, OnDestroy {
  private readonly api = inject(RippleApiService);

  readonly metrics = signal<TypeMetric[]>([]);
  readonly instances = signal<ClusterInstance[]>([]);
  readonly queue = signal<QueueSnapshot | null>(null);
  readonly queueRefreshSeconds = 5;
  private queueTimer: ReturnType<typeof setInterval> | null = null;



  readonly maxShare = computed(() => Math.max(1, ...this.metrics().map((m) => m.share ?? 0)));
  readonly maxAvg = computed(() => Math.max(1, ...this.metrics().map((m) => m.avgMs ?? 0)));
  readonly maxWait = computed(() => Math.max(1, ...this.metrics().map((m) => m.avgWaitMs ?? 0)));
  // Rates are small fractions, so the bar scales to the largest observed rate (epsilon floor, not 1).
  readonly maxRetryRate = computed(() => Math.max(0.0001, ...this.metrics().map((m) => m.avgRetryRate ?? 0)));
  readonly maxExec = computed(() => Math.max(1, ...this.instances().map((i) => i.executing)));

  readonly liveCount = computed(() => this.instances().filter((i) => !this.isStale(i)).length);
  readonly totalExecuting = computed(() =>
    this.instances().filter((i) => !this.isStale(i)).reduce((sum, i) => sum + i.executing, 0)
  );

  ngOnInit(): void {
    this.api.getTypeMetrics().subscribe((m) => this.metrics.set(m.filter((t) => t.typeKey !== DEFAULT_TYPE_KEY)));
    this.api.getCluster().subscribe((c) => this.instances.set(c.instances));
    this.loadQueue();
    // Only the queue card polls: it is one cheap read over the active waves, and a drain estimate that never
    // moves is not much of an estimate. The rest of the page is per-compaction data that changes slowly.
    this.queueTimer = setInterval(() => this.loadQueue(), this.queueRefreshSeconds * 1000);
  }

  ngOnDestroy(): void {
    if (this.queueTimer) {
      clearInterval(this.queueTimer);
    }
  }

  private loadQueue(): void {
    this.api.getQueue().subscribe((q) => this.queue.set(q));
  }

  duration(seconds: number): string {
    return formatDuration(seconds);
  }

  pct(value: number | null, max: number): number {
    if (value == null || max <= 0) {
      return 0;
    }
    return Math.max(2, Math.round((value * 100) / max));
  }

  execPct(i: ClusterInstance): number {
    return this.isStale(i) ? 0 : Math.round((i.executing * 100) / this.maxExec());
  }

  isStale(i: ClusterInstance): boolean {
    return i.ageSeconds > STALE_SECONDS;
  }
}
