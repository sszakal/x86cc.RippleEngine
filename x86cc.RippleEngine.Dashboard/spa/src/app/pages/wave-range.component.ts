import { DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output, computed, inject, signal } from '@angular/core';
import { RippleApiService } from '../core/ripple-api.service';

const DAY = 86_400_000;

interface Bar {
  x: number; // 0..1 position in the domain
  h: number; // 0..1 normalized height
  color: string;
}

/**
 * A stock-chart-style time-range brush: preset buttons + a dual-handle slider dragged over a mini activity
 * histogram (wave counts per day). Emits the selected [from, to] as ISO instants whenever it settles.
 */
@Component({
  selector: 'app-wave-range',
  standalone: true,
  imports: [DatePipe],
  styles: [
    `
      .dual { position: relative; height: 20px; }
      .dual input[type='range'] {
        position: absolute; inset: 0; width: 100%; height: 20px; margin: 0;
        -webkit-appearance: none; appearance: none; background: transparent; pointer-events: none;
      }
      .dual input[type='range']::-webkit-slider-thumb {
        -webkit-appearance: none; appearance: none; pointer-events: auto;
        height: 16px; width: 16px; border-radius: 9999px; background: #465fff;
        border: 2px solid #fff; box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.2); cursor: grab;
      }
      .dual input[type='range']::-moz-range-thumb {
        pointer-events: auto; height: 16px; width: 16px; border-radius: 9999px;
        background: #465fff; border: 2px solid #fff; cursor: grab;
      }
      .dual input[type='range']::-webkit-slider-runnable-track { background: transparent; }
      .dual input[type='range']::-moz-range-track { background: transparent; }
    `
  ],
  template: `
    <div class="card mb-4">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div class="flex gap-1">
          @for (p of presets; track p.days) {
            <button class="rounded-md border px-2.5 py-1 text-xs font-medium"
                    [class]="isPreset(p.days) ? 'border-brand-500 bg-brand-50 text-brand-700' : 'border-gray-300 text-gray-600 hover:bg-gray-50'"
                    (click)="preset(p.days)">{{ p.label }}</button>
          }
        </div>
        <div class="text-sm font-medium text-gray-600">{{ fromDate() | date: 'mediumDate' }} — {{ toDate() | date: 'mediumDate' }}</div>
      </div>

      <div class="select-none">
        <!-- mini activity histogram + selected region -->
        <div class="relative h-12">
          @for (bar of bars(); track $index) {
            <div class="absolute bottom-0 w-[3px] -translate-x-1/2 rounded-sm"
                 [style.left.%]="bar.x * 100" [style.height.%]="bar.h * 100" [style.background]="bar.color"></div>
          }
          <div class="pointer-events-none absolute bottom-0 top-0 border-x border-brand-400 bg-brand-500/10"
               [style.left.%]="fromPct()" [style.right.%]="toPctInv()"></div>
        </div>

        <!-- dual-handle slider -->
        <div class="dual mt-1">
          <div class="pointer-events-none absolute inset-x-0 top-1/2 h-[3px] -translate-y-1/2 rounded-full bg-gray-200"></div>
          <div class="pointer-events-none absolute top-1/2 h-[3px] -translate-y-1/2 rounded-full bg-brand-500"
               [style.left.%]="fromPct()" [style.right.%]="toPctInv()"></div>
          <input type="range" min="0" [max]="steps()" [value]="fromStep()" (input)="onFrom($event)" (change)="emit()" aria-label="Range start" />
          <input type="range" min="0" [max]="steps()" [value]="toStep()" (input)="onTo($event)" (change)="emit()" aria-label="Range end" />
        </div>
      </div>
    </div>
  `
})
export class WaveRangeComponent implements OnInit {
  private readonly api = inject(RippleApiService);

  /** Optional initial selection (ISO) — e.g. seeded from a URL range when opened from the Activity drill-down. */
  @Input() initialFrom = '';
  @Input() initialTo = '';

  @Output() rangeChange = new EventEmitter<{ from: string; to: string }>();

  readonly presets = [
    { label: '1W', days: 7 },
    { label: '1M', days: 30 },
    { label: '3M', days: 90 },
    { label: '1Y', days: 365 },
    { label: 'All', days: 0 } // 0 ⇒ full domain
  ];

  private readonly domainStart = signal(new Date());
  readonly steps = signal(1);
  readonly fromStep = signal(0);
  readonly toStep = signal(1);
  readonly bars = signal<Bar[]>([]);

  readonly fromDate = computed(() => this.stepDate(this.fromStep()));
  readonly toDate = computed(() => this.stepDate(this.toStep()));
  readonly fromPct = computed(() => (this.fromStep() / this.steps()) * 100);
  readonly toPctInv = computed(() => (1 - this.toStep() / this.steps()) * 100);

  ngOnInit(): void {
    // The domain = the actual span of wave history (earliest wave day → today), with a daily backdrop.
    const today = startOfDay(new Date());
    const wideFrom = new Date(2020, 0, 1).toISOString();
    const wideTo = addDays(today, 1).toISOString();
    this.api.getWavesHistogram(wideFrom, wideTo, 'day').subscribe((r) => {
      const ds = r.buckets.length ? startOfDay(new Date(r.buckets[0].start)) : addDays(today, -365);
      const de = addDays(today, 1); // exclusive end so "today" is fully included
      const steps = Math.max(1, Math.round((de.getTime() - ds.getTime()) / DAY));
      const span = de.getTime() - ds.getTime();
      const max = Math.max(1, ...r.buckets.map((b) => b.count));

      this.domainStart.set(ds);
      this.steps.set(steps);
      this.bars.set(
        r.buckets.map((b) => {
          const t = new Date(b.start).getTime();
          return {
            x: Math.max(0, Math.min(1, (t - ds.getTime()) / span)),
            h: b.count / max,
            color: b.faulted > 0 ? '#e28a8a' : b.running > 0 ? '#fbcf67' : '#7ccf7c'
          };
        })
      );

      // Seed the selection: the URL range if given, else the whole domain.
      this.fromStep.set(this.initialFrom ? this.dateToStep(this.initialFrom) : 0);
      this.toStep.set(this.initialTo ? this.dateToStep(this.initialTo) : steps);
      this.emit();
    });
  }

  onFrom(e: Event): void {
    const v = +(e.target as HTMLInputElement).value;
    this.fromStep.set(Math.min(v, this.toStep()));
  }

  onTo(e: Event): void {
    const v = +(e.target as HTMLInputElement).value;
    this.toStep.set(Math.max(v, this.fromStep()));
  }

  preset(days: number): void {
    this.fromStep.set(days === 0 ? 0 : Math.max(0, this.steps() - days));
    this.toStep.set(this.steps());
    this.emit();
  }

  isPreset(days: number): boolean {
    return this.toStep() === this.steps() && this.fromStep() === (days === 0 ? 0 : Math.max(0, this.steps() - days));
  }

  emit(): void {
    this.rangeChange.emit({ from: this.fromDate().toISOString(), to: this.toDate().toISOString() });
  }

  private stepDate(i: number): Date {
    return new Date(this.domainStart().getTime() + i * DAY);
  }

  private dateToStep(iso: string): number {
    const step = Math.round((new Date(iso).getTime() - this.domainStart().getTime()) / DAY);
    return Math.max(0, Math.min(this.steps(), step));
  }
}

function startOfDay(d: Date): Date {
  const x = new Date(d);
  x.setHours(0, 0, 0, 0);
  return x;
}

function addDays(d: Date, n: number): Date {
  const x = new Date(d);
  x.setDate(x.getDate() + n);
  return x;
}
