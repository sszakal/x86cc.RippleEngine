import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { HistogramBucket } from '../../core/models';
import { WaveHistogramService } from './wave-histogram.service';
import { EMPTY, hourLabel, rampColor } from './wave-palette';
import { dayRange, segmentsOf } from './wave-time';

interface HourCell {
  hour: number; // 0–23
  bucket: HistogramBucket | null;
}

/** /waves/:year/:month/:day — the day's fixed 24-hour grid; every hour with waves links into its view. */
@Component({
  selector: 'app-day-page',
  standalone: true,
  imports: [DecimalPipe, RouterLink],
  template: `
    <div>
      <div class="grid grid-cols-4 gap-2 sm:grid-cols-6 lg:grid-cols-8">
        @for (c of cells(); track c.hour) {
          @if (c.bucket; as b) {
            <a
              class="flex h-16 flex-col justify-between rounded-lg p-2 text-left ring-1 ring-inset ring-black/5 transition-transform hover:scale-[1.04]"
              [style.background]="rampColor(b.count, b.faulted, b.running)"
              [title]="hourLabel(c.hour) + ' — ' + b.count + ' waves'" [routerLink]="hourLink(c.hour)">
              <span class="text-sm font-semibold text-gray-900">{{ hourLabel(c.hour) }}</span>
              <span class="text-xs text-gray-700">{{ b.count | number }}</span>
            </a>
          } @else {
            <div class="flex h-16 flex-col justify-between rounded-lg p-2 ring-1 ring-inset ring-black/5"
                 [style.background]="emptyColor" [title]="hourLabel(c.hour) + ' — no waves'">
              <span class="text-sm font-medium text-gray-400">{{ hourLabel(c.hour) }}</span>
              <span class="text-xs text-gray-300">0</span>
            </div>
          }
        }
      </div>
    </div>
  `
})
export class DayPageComponent implements OnInit, OnDestroy {
  private readonly hist = inject(WaveHistogramService);
  private readonly route = inject(ActivatedRoute);
  private sub?: Subscription;

  private year = 0;
  private month = 1;
  private day = 1;

  readonly cells = signal<HourCell[]>(emptyGrid());

  protected readonly rampColor = rampColor;
  protected readonly hourLabel = hourLabel;
  protected readonly emptyColor = EMPTY;

  ngOnInit(): void {
    this.sub = this.route.paramMap.subscribe((pm) => {
      const { year = 0, month = 1, day = 1 } = segmentsOf(pm);
      this.year = year;
      this.month = month;
      this.day = day;
      const { from, to } = dayRange(year, month, day);
      this.cells.set(emptyGrid());
      this.hist.histogram(from.toISOString(), to.toISOString(), 'hour').subscribe((r) => {
        const byHour = new Map<number, HistogramBucket>();
        for (const b of r.buckets) {
          byHour.set(new Date(b.start).getHours(), b);
        }
        this.cells.set(Array.from({ length: 24 }, (_, hour) => ({ hour, bucket: byHour.get(hour) ?? null })));
      });
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  hourLink(hour: number): unknown[] {
    return ['/waves', this.year, this.month, this.day, hour];
  }
}

function emptyGrid(): HourCell[] {
  return Array.from({ length: 24 }, (_, hour) => ({ hour, bucket: null }));
}
