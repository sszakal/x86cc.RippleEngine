import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, EventEmitter, Input, OnDestroy, Output, inject, signal } from '@angular/core';
import { RippleApiService } from '../../core/ripple-api.service';
import { Wave, WaveStatus } from '../../core/models';
import { statusClass } from './wave-palette';

/**
 * The wave-detail modal. Deep-linkable by id (the shell shows it when `?wave=<id>` is present), so it takes a
 * `waveId` and fetches its own data. Owns its full lifecycle: initial load, auto-refresh polling while the wave
 * is live, and the CSV download. Emits `closed` when the user dismisses it.
 */
@Component({
  selector: 'app-wave-popup',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="fixed inset-0 z-20 flex items-center justify-center bg-black/40 p-4" (click)="closed.emit()">
      <div class="w-full max-w-lg rounded-2xl bg-white p-6 shadow-xl" (click)="$event.stopPropagation()">
        @if (wave(); as w) {
          <div class="mb-4 flex items-start justify-between gap-3">
            <div>
              <h2 class="text-lg font-semibold text-gray-900">{{ w.name }}</h2>
              <div class="mt-1 text-xs text-gray-500">{{ w.type ?? '—' }}</div>
            </div>
            <div class="flex flex-col items-end gap-2">
              <span class="badge" [class]="statusClass(w.status)">{{ w.status }}</span>
              <!-- Auto-refresh: only meaningful while the wave is still live (terminal waves never change). -->
              @if (!isTerminal(w.status)) {
                <div class="flex items-center gap-1 text-[11px] text-gray-400">
                  <span class="mr-0.5">Auto-refresh</span>
                  @for (opt of refreshOptions; track opt.label) {
                    <button
                      class="rounded px-1.5 py-0.5 ring-1 ring-inset ring-gray-200 hover:bg-gray-50"
                      [class.bg-brand-500]="refreshSeconds() === opt.value"
                      [class.text-white]="refreshSeconds() === opt.value"
                      [class.ring-brand-500]="refreshSeconds() === opt.value"
                      (click)="setRefresh(opt.value)">{{ opt.label }}</button>
                  }
                </div>
              }
            </div>
          </div>

          <!-- Progress -->
          <div class="mb-4">
            <div class="mb-1 flex items-center justify-between text-xs">
              <span class="text-gray-500">Progress</span>
              <span class="font-medium text-gray-700">{{ w.succeeded | number }} / {{ w.rippleCount | number }} succeeded ({{ progress(w) }}%)</span>
            </div>
            <div class="h-2 w-full overflow-hidden rounded-full bg-gray-200">
              <div class="h-full rounded-full bg-emerald-500" [style.width.%]="progress(w)"></div>
            </div>
          </div>

          <dl class="grid grid-cols-2 gap-x-6 gap-y-3 text-sm">
            <div><dt class="text-gray-500">Ripples</dt><dd class="font-medium text-gray-900">{{ w.rippleCount | number }}</dd></div>
            <div><dt class="text-gray-500">Succeeded</dt><dd class="font-medium text-emerald-600">{{ w.succeeded | number }}</dd></div>
            <div><dt class="text-gray-500">Failed</dt><dd class="font-medium text-rose-600">{{ w.failed | number }}</dd></div>
            <div><dt class="text-gray-500">Retries</dt><dd class="font-medium text-gray-900">{{ w.retryCount | number }}</dd></div>
            <div><dt class="text-gray-500">Pending / Running</dt><dd class="font-medium text-amber-500">{{ w.pending | number }} / {{ w.running | number }}</dd></div>
            <!-- Only shown when non-zero: parked work is the exceptional state, but while it IS parked it is the
                 whole explanation for a stalled-looking wave (0 pending, 0 running, progress short of 100%). -->
            @if (w.paused > 0) {
              <div><dt class="text-gray-500">Paused</dt><dd class="font-medium text-amber-600">{{ w.paused | number }}</dd></div>
            }
            <div><dt class="text-gray-500">Avg time</dt><dd class="font-medium text-gray-900">{{ w.avgDurationMs != null ? w.avgDurationMs + ' ms' : '—' }}</dd></div>
            <div><dt class="text-gray-500">Duration</dt><dd class="font-medium text-gray-900">{{ w.durationMs != null ? (w.durationMs / 1000 | number: '1.0-1') + ' s' : '—' }}</dd></div>
            <div><dt class="text-gray-500">Throughput</dt><dd class="font-medium text-gray-900">{{ w.throughput != null ? w.throughput + ' /s' : '—' }}</dd></div>
            <div><dt class="text-gray-500">Report samples</dt><dd class="font-medium text-gray-900">{{ w.splashSampleCount | number }}</dd></div>
            <div><dt class="text-gray-500">Created</dt><dd class="font-medium text-gray-900">{{ w.createdAt | date: 'short' }}</dd></div>
            <div><dt class="text-gray-500">Completed</dt><dd class="font-medium text-gray-900">{{ w.completedAt ? (w.completedAt | date: 'short') : '—' }}</dd></div>
            <div><dt class="text-gray-500">Compacted</dt><dd class="font-medium text-gray-900">{{ w.compactedAt ? (w.compactedAt | date: 'short') : '—' }}</dd></div>
          </dl>
          <div class="mt-6 flex items-center justify-end gap-2">
            <button class="btn" (click)="closed.emit()">Close</button>
            @if (w.rippleCount > 0 && w.compactedAt) {
              <button class="btn bg-brand-500 text-white hover:bg-brand-600" [disabled]="downloading()" (click)="downloadReport(w)">
                {{ downloading() ? 'Preparing…' : 'Download report (CSV)' }}
              </button>
            }
          </div>
        } @else {
          <div class="py-8 text-center text-sm text-gray-400">Loading wave…</div>
        }
      </div>
    </div>
  `
})
export class WavePopupComponent implements OnDestroy {
  private readonly api = inject(RippleApiService);

  readonly wave = signal<Wave | null>(null);
  readonly downloading = signal(false);

  // Auto-refresh cadence. null = off; polling only runs while the wave is non-terminal.
  readonly refreshOptions: ReadonlyArray<{ label: string; value: number | null }> = [
    { label: '2s', value: 2 },
    { label: '5s', value: 5 },
    { label: '10s', value: 10 },
    { label: 'Off', value: null }
  ];
  readonly refreshSeconds = signal<number | null>(5);
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private currentId = '';

  @Input({ required: true }) set waveId(id: string) {
    if (id === this.currentId) {
      return;
    }
    this.currentId = id;
    this.wave.set(null);
    this.refresh(); // fetch fresh detail, then poll on cadence
    this.startPolling();
  }

  @Output() closed = new EventEmitter<void>();

  ngOnDestroy(): void {
    this.stopPolling();
  }

  setRefresh(seconds: number | null): void {
    this.refreshSeconds.set(seconds);
    this.startPolling();
  }

  isTerminal(status: WaveStatus): boolean {
    return status === 'Completed' || status === 'Faulted';
  }

  progress(w: Wave): number {
    return w.rippleCount > 0 ? Math.round((w.succeeded * 100) / w.rippleCount) : 0;
  }

  downloadReport(w: Wave): void {
    this.downloading.set(true);
    this.api.downloadReportCsv(w.id).subscribe({
      next: () => this.downloading.set(false),
      error: () => this.downloading.set(false)
    });
  }

  /** (Re)arm the poll timer — only while a cadence is chosen and the wave is live. */
  private startPolling(): void {
    this.stopPolling();
    const seconds = this.refreshSeconds();
    const w = this.wave();
    if (seconds == null || (w && this.isTerminal(w.status))) {
      return;
    }
    this.pollTimer = setInterval(() => this.refresh(), seconds * 1000);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  /** Re-fetch the wave; stop polling once it has gone terminal (its numbers can no longer change). */
  private refresh(): void {
    const id = this.currentId;
    if (!id) {
      return;
    }
    this.api.getWave(id).subscribe((w) => {
      if (this.currentId === w.id) {
        this.wave.set(w);
        if (this.isTerminal(w.status)) {
          this.stopPolling();
        }
      }
    });
  }

  protected readonly statusClass = statusClass;
}
