import { DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RippleApiService } from '../core/ripple-api.service';
import { DefaultSetting, EngineInfo, TypeSetting } from '../core/models';

/** The reserved default-row key (matches RippleTypeKey.Default on the server). */
const DEFAULT_KEY = '__default__';

interface Draft {
  batchSize: number;
  gapSeconds: number;
  maxAttempts: number | null;
}

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [DecimalPipe],
  template: `
    <div class="mb-6">
      <h1 class="text-2xl font-semibold text-gray-900">Settings</h1>
      <p class="mt-1 text-sm text-gray-500">
        The scheduler's per-type config, stored in <code class="text-xs">ripple.type_schedule</code>. Edits take
        effect on the next fan-out (batch/gap are baked into a wave's <code class="text-xs">schedule_order</code>
        when it's created); <code class="text-xs">max attempts</code> applies live at claim time.
      </p>
    </div>

    @if (error()) {
      <div class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-700">{{ error() }}</div>
    }

    <!-- Default row: the fall-back every unconfigured type inherits -->
    <div class="card mb-6">
      <div class="mb-1 text-sm font-medium text-gray-700">Default configuration</div>
      <p class="mb-4 text-xs text-gray-400">
        Every type with no config of its own inherits these values. Seeded by the database migration; editable here.
      </p>
      @if (def(); as d) {
        <div class="flex flex-wrap items-end gap-4">
          <label class="text-xs text-gray-500">
            Batch size
            <input type="number" min="1" class="mt-1 block w-28 rounded border border-gray-300 px-2 py-1 text-sm text-gray-900"
                   [value]="d.batchSize" (input)="editNum(DEFAULT_KEY, 'batchSize', $event)" />
          </label>
          <label class="text-xs text-gray-500">
            Gap (seconds)
            <input type="number" min="0" step="0.1" class="mt-1 block w-28 rounded border border-gray-300 px-2 py-1 text-sm text-gray-900"
                   [value]="d.gapSeconds" (input)="editNum(DEFAULT_KEY, 'gapSeconds', $event)" />
          </label>
          <label class="text-xs text-gray-500">
            Max attempts
            <input type="number" min="1" class="mt-1 block w-28 rounded border border-gray-300 px-2 py-1 text-sm text-gray-900"
                   [value]="d.maxAttempts" (input)="editMax(DEFAULT_KEY, $event)" />
          </label>
          <button class="rounded-md bg-brand-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-brand-700 disabled:opacity-50" [disabled]="savingKey() === DEFAULT_KEY" (click)="save(DEFAULT_KEY)">
            {{ savingKey() === DEFAULT_KEY ? 'Saving…' : 'Save default' }}
          </button>
          @if (savedKey() === DEFAULT_KEY) { <span class="text-xs font-medium text-emerald-600">Saved ✓</span> }
        </div>
      }
    </div>

    <!-- Per-type configuration -->
    <div class="card mb-6">
      <div class="mb-1 text-sm font-medium text-gray-700">Per-type configuration</div>
      <p class="mb-4 text-xs text-gray-400">
        One row per registered <span class="font-mono">wave|ripple</span> handler. A configured type overrides the
        default; <strong>Reset</strong> deletes its row so it re-inherits the default.
      </p>
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-gray-200 text-left text-xs text-gray-500">
              <th class="py-2 pr-4 font-medium">Type</th>
              <th class="px-2 py-2 font-medium">Batch</th>
              <th class="px-2 py-2 font-medium">Gap (s)</th>
              <th class="px-2 py-2 font-medium">Max attempts</th>
              <th class="px-2 py-2 font-medium">Status</th>
              <th class="py-2 pl-2 font-medium text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            @for (t of types(); track t.typeKey) {
              <tr class="border-b border-gray-100 align-top">
                <td class="py-3 pr-4">
                  <div class="font-mono text-xs text-gray-800">{{ t.typeKey }}</div>
                  <div class="mt-0.5 text-[11px] text-gray-400">code default: {{ seedLabel(t) }}</div>
                </td>
                <td class="px-2 py-3">
                  <input type="number" min="1" class="w-20 rounded border border-gray-300 px-2 py-1 text-sm text-gray-900"
                         [value]="draft(t.typeKey)?.batchSize" (input)="editNum(t.typeKey, 'batchSize', $event)" />
                </td>
                <td class="px-2 py-3">
                  <input type="number" min="0" step="0.1" class="w-20 rounded border border-gray-300 px-2 py-1 text-sm text-gray-900"
                         [value]="draft(t.typeKey)?.gapSeconds" (input)="editNum(t.typeKey, 'gapSeconds', $event)" />
                </td>
                <td class="px-2 py-3">
                  <input type="number" min="1" placeholder="default" class="w-24 rounded border border-gray-300 px-2 py-1 text-sm text-gray-900"
                         [value]="draft(t.typeKey)?.maxAttempts" (input)="editMax(t.typeKey, $event)" />
                </td>
                <td class="px-2 py-3">
                  @switch (t.pauseState) {
                    @case ('paused') {
                      <span class="rounded-full bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-700">Paused</span>
                    }
                    @case ('resuming_rebase') {
                      <span class="rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700">Resuming…</span>
                    }
                    @case ('resuming_asis') {
                      <span class="rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700">Resuming…</span>
                    }
                    @default {
                      @if (t.configured) {
                        <span class="rounded-full bg-brand-50 px-2 py-0.5 text-[11px] font-medium text-brand-700">Configured</span>
                      } @else {
                        <span class="rounded-full bg-gray-100 px-2 py-0.5 text-[11px] font-medium text-gray-500">Inherits default</span>
                      }
                    }
                  }
                  @if (savedKey() === t.typeKey) { <span class="ml-1 text-[11px] font-medium text-emerald-600">Saved ✓</span> }
                </td>
                <td class="py-3 pl-2 text-right whitespace-nowrap">
                  @if (resumingKey() === t.typeKey) {
                    <!-- Resume confirm: rebase to the current frontier (fair) vs run ahead as-is (catch up) -->
                    <span class="mr-1 text-[11px] text-gray-500">Resume:</span>
                    <button class="rounded-md bg-brand-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-brand-700 disabled:opacity-50"
                            [disabled]="savingKey() === t.typeKey"
                            title="Re-stamp the parked work onto the current queue frontier so it interleaves fairly with running jobs."
                            (click)="resume(t.typeKey, true)">From now</button>
                    <button class="ml-1 rounded-md border border-amber-300 px-2.5 py-1.5 text-xs font-medium text-amber-700 hover:bg-amber-50 disabled:opacity-40"
                            [disabled]="savingKey() === t.typeKey"
                            title="Resume at its old queue position — it will run ahead of everything to catch up, monopolising workers."
                            (click)="resume(t.typeKey, false)">As-is</button>
                    <button class="ml-1 rounded-md px-2 py-1.5 text-xs font-medium text-gray-500 hover:bg-gray-50" (click)="resumingKey.set(null)">Cancel</button>
                  } @else {
                    @if (t.pauseState === 'paused') {
                      <button class="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-amber-700 disabled:opacity-50" [disabled]="savingKey() === t.typeKey" (click)="resumingKey.set(t.typeKey)">Resume</button>
                    } @else {
                      <!-- active OR still draining a resume: Pause (re-)parks the type -->
                      <button class="rounded-md border border-amber-300 px-3 py-1.5 text-xs font-medium text-amber-700 hover:bg-amber-50 disabled:opacity-50" [disabled]="savingKey() === t.typeKey" (click)="pause(t.typeKey)">Pause</button>
                    }
                    <button class="ml-1 rounded-md bg-brand-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-brand-700 disabled:opacity-50" [disabled]="savingKey() === t.typeKey" (click)="save(t.typeKey)">Save</button>
                    <button class="ml-1 rounded-md border border-gray-300 px-3 py-1.5 text-xs font-medium text-gray-600 hover:bg-gray-50 disabled:opacity-40" [disabled]="!t.configured || savingKey() === t.typeKey" (click)="reset(t.typeKey)">Reset</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="py-4 text-sm text-gray-400">No handlers registered.</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>

    <!-- Read-only engine options (per-instance) -->
    @if (engine(); as e) {
      <div class="card">
        <div class="mb-1 text-sm font-medium text-gray-700">Engine (read-only)</div>
        <p class="mb-4 text-xs text-gray-400">
          Per-instance runtime knobs for this worker (<span class="font-mono text-[11px]">{{ e.instanceId }}</span>),
          set via environment/host config — not stored in the database.
        </p>
        <dl class="grid grid-cols-2 gap-x-8 gap-y-2 text-sm sm:grid-cols-3">
          <div class="flex justify-between"><dt class="text-gray-500">Max concurrency</dt><dd class="font-medium text-gray-900">{{ e.maxConcurrency }}</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Prefetch factor</dt><dd class="font-medium text-gray-900">{{ e.prefetchFactor }}</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Claim batch size</dt><dd class="font-medium text-gray-900">{{ e.claimBatchSize }}</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Execution timeout</dt><dd class="font-medium text-gray-900">{{ e.executionTimeoutSeconds }}s</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Heartbeat timeout</dt><dd class="font-medium text-gray-900">{{ e.heartbeatTimeoutSeconds }}s</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Stats refresh</dt><dd class="font-medium text-gray-900">{{ e.waveStatsRefreshSeconds }}s</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Compaction</dt><dd class="font-medium text-gray-900">{{ e.compactionSeconds }}s</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Report chunk size</dt><dd class="font-medium text-gray-900">{{ e.reportChunkSize | number }}</dd></div>
          <div class="flex justify-between"><dt class="text-gray-500">Default retention</dt><dd class="font-medium text-gray-900">{{ e.defaultRetentionDays == null ? 'forever' : e.defaultRetentionDays + ' days' }}</dd></div>
        </dl>
        @if (retention().length) {
          <div class="mt-4 border-t border-gray-100 pt-3">
            <div class="mb-2 text-xs font-medium text-gray-500">Retention by wave type</div>
            <div class="space-y-1">
              @for (r of retention(); track r.type) {
                <div class="flex justify-between text-sm">
                  <span class="font-mono text-xs text-gray-600">{{ r.type }}</span>
                  <span class="font-medium text-gray-900">{{ r.days == null ? 'forever' : r.days + ' days' }}</span>
                </div>
              }
            </div>
          </div>
        }
      </div>
    }
  `
})
export class SettingsComponent implements OnInit {
  private readonly api = inject(RippleApiService);

  readonly DEFAULT_KEY = DEFAULT_KEY;

  readonly types = signal<TypeSetting[]>([]);
  readonly def = signal<DefaultSetting | null>(null);
  readonly engine = signal<EngineInfo | null>(null);
  readonly drafts = signal<Record<string, Draft>>({});
  readonly savingKey = signal<string | null>(null);
  readonly savedKey = signal<string | null>(null);
  readonly resumingKey = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly retention = computed(() => {
    const map = this.engine()?.retentionByWaveType ?? {};
    return Object.entries(map).map(([type, days]) => ({ type, days }));
  });

  ngOnInit(): void {
    this.load();
    this.api.getEngineInfo().subscribe((e) => this.engine.set(e));
  }

  private load(): void {
    this.api.getSettings().subscribe((res) => {
      this.def.set(res.default);
      this.types.set(res.types);

      const drafts: Record<string, Draft> = {};
      if (res.default) {
        drafts[DEFAULT_KEY] = { ...res.default };
      }
      const fallback = res.default ?? { batchSize: 1, gapSeconds: 1, maxAttempts: null as number | null };
      for (const t of res.types) {
        drafts[t.typeKey] = t.configured
          ? { batchSize: t.batchSize!, gapSeconds: t.gapSeconds!, maxAttempts: t.maxAttempts }
          : { batchSize: fallback.batchSize, gapSeconds: fallback.gapSeconds, maxAttempts: null };
      }
      this.drafts.set(drafts);
    });
  }

  draft(key: string): Draft | undefined {
    return this.drafts()[key];
  }

  editNum(key: string, field: 'batchSize' | 'gapSeconds', ev: Event): void {
    const value = Number((ev.target as HTMLInputElement).value);
    this.patch(key, { [field]: value });
  }

  editMax(key: string, ev: Event): void {
    const raw = (ev.target as HTMLInputElement).value.trim();
    this.patch(key, { maxAttempts: raw === '' ? null : Number(raw) });
  }

  private patch(key: string, patch: Partial<Draft>): void {
    this.drafts.update((m) => ({ ...m, [key]: { ...m[key], ...patch } }));
  }

  seedLabel(t: TypeSetting): string {
    if (t.seededBatchSize == null) {
      return 'none';
    }
    const max = t.seededMaxAttempts == null ? '—' : t.seededMaxAttempts;
    return `${t.seededBatchSize} / ${t.seededGapSeconds}s / ${max}`;
  }

  save(key: string): void {
    const d = this.drafts()[key];
    if (!d) {
      return;
    }
    if (!(d.batchSize >= 1) || !(d.gapSeconds > 0) || (d.maxAttempts != null && d.maxAttempts < 1)) {
      this.error.set('Batch ≥ 1, gap > 0, and max attempts ≥ 1 (or blank to inherit the default).');
      return;
    }
    if (key === DEFAULT_KEY && d.maxAttempts == null) {
      this.error.set('The default configuration needs an explicit max attempts.');
      return;
    }
    this.error.set(null);
    this.savingKey.set(key);
    this.api
      .updateTypeSetting(key, { batchSize: d.batchSize, gapSeconds: d.gapSeconds, maxAttempts: d.maxAttempts })
      .subscribe({
        next: () => {
          this.savingKey.set(null);
          this.flashSaved(key);
          this.load();
        },
        error: () => {
          this.savingKey.set(null);
          this.error.set('Save failed.');
        }
      });
  }

  reset(typeKey: string): void {
    this.savingKey.set(typeKey);
    this.api.resetTypeSetting(typeKey).subscribe({
      next: () => {
        this.savingKey.set(null);
        this.load();
      },
      error: () => {
        this.savingKey.set(null);
        this.error.set('Reset failed.');
      }
    });
  }

  pause(typeKey: string): void {
    this.error.set(null);
    this.savingKey.set(typeKey);
    this.api.pauseType(typeKey).subscribe({
      next: () => {
        this.savingKey.set(null);
        this.load();
      },
      error: () => {
        this.savingKey.set(null);
        this.error.set('Pause failed.');
      }
    });
  }

  resume(typeKey: string, rebase: boolean): void {
    this.error.set(null);
    this.resumingKey.set(null);
    this.savingKey.set(typeKey);
    this.api.resumeType(typeKey, rebase).subscribe({
      next: () => {
        this.savingKey.set(null);
        this.load();
      },
      error: () => {
        this.savingKey.set(null);
        this.error.set('Resume failed.');
      }
    });
  }

  private flashSaved(key: string): void {
    this.savedKey.set(key);
    setTimeout(() => this.savedKey.set(null), 2000);
  }
}
