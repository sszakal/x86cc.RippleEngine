import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, catchError, fromEvent, of, startWith, switchMap, timer } from 'rxjs';
import { RippleApiService } from './ripple-api.service';

const POLL_MS = 5_000;

/**
 * Keep reading "active" for this long after the last non-zero sample. Each worker writes its in-memory
 * executing count on every heartbeat, so a *trickling* wave (a type with a small batch over a long gap)
 * genuinely sits at zero in-flight between batches. Without the hold, the mark would blink idle/active
 * across those gaps and read as broken rather than as a status.
 */
const IDLE_HOLD_MS = 12_000;

/**
 * Is the cluster doing anything right now? Polls the heartbeat table's summed `executing` — the true,
 * low-latency in-flight count (see the sample WebAPI's /cluster endpoint) and the smallest payload of any
 * read endpoint. Drives the sidebar's ripple mark; nothing else depends on it, so it fails soft.
 */
@Injectable({ providedIn: 'root' })
export class EngineActivityService {
  private readonly api = inject(RippleApiService);
  private lastBusyAt = 0;

  /** True while ripples are executing anywhere in the cluster (with a short tail — see IDLE_HOLD_MS). */
  readonly active = signal(false);

  constructor() {
    fromEvent(document, 'visibilitychange')
      .pipe(
        startWith(null),
        // A background tab has nothing to show; stop polling until it comes back, then sample immediately.
        switchMap(() => (document.visibilityState === 'hidden' ? EMPTY : timer(0, POLL_MS))),
        // catchError sits on the INNER request so one failed poll degrades to "idle" instead of tearing
        // down the stream (the dev server has no backend at all — the mark just stays static).
        switchMap(() => this.api.getCluster().pipe(catchError(() => of({ instances: [] })))),
        takeUntilDestroyed()
      )
      .subscribe((cluster) => {
        const executing = cluster.instances.reduce((sum, i) => sum + i.executing, 0);
        const now = Date.now();
        if (executing > 0) {
          this.lastBusyAt = now;
        }
        this.active.set(now - this.lastBusyAt < IDLE_HOLD_MS);
      });
  }
}
