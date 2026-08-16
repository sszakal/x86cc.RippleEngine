import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { WaveActivityDay } from '../../core/models';
import { WaveHistogramService } from './wave-histogram.service';
import { EMPTY, rampColor, tooltip } from './wave-palette';
import { monthRange, segmentsOf, startOfDay, toKey } from './wave-time';

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

interface DayCell {
  day: number; // day of month (0 = leading weekday padding)
  date: Date | null;
  future: boolean;
  data: WaveActivityDay | null;
}

/** /waves/:year/:month — a single month's calendar; every day with waves links into its 24-hour grid. */
@Component({
  selector: 'app-month-page',
  standalone: true,
  imports: [DecimalPipe, RouterLink],
  template: `
    <div>
      <div class="grid grid-cols-7 gap-2" style="max-width: 640px">
        @for (w of weekdays; track w) {
          <div class="pb-1 text-center text-[11px] font-medium text-gray-500">{{ w }}</div>
        }
        @for (c of cells(); track $index) {
          @if (c.day === 0) {
            <div></div>
          } @else if (c.future) {
            <div class="flex h-14 flex-col justify-between rounded-lg p-2 ring-1 ring-inset ring-black/5"
                 [style.background]="futureColor" [title]="c.date && futureTitle(c.date)">
              <span class="text-xs font-medium text-gray-400">{{ c.day }}</span>
            </div>
          } @else if (c.data && c.data.count > 0) {
            <a class="flex h-14 flex-col justify-between rounded-lg p-2 text-left ring-1 ring-inset ring-black/5 transition-transform hover:scale-[1.04]"
               [style.background]="rampColor(c.data.count, c.data.faulted, c.data.running)"
               [title]="c.date && tooltip(c.date, c.data)" [routerLink]="dayLink(c.day)">
              <span class="text-xs font-semibold text-gray-900">{{ c.day }}</span>
              <span class="text-xs text-gray-700">{{ c.data.count | number }}</span>
            </a>
          } @else {
            <div class="flex h-14 flex-col justify-between rounded-lg p-2 ring-1 ring-inset ring-black/5"
                 [style.background]="emptyColor" [title]="c.date && tooltip(c.date, c.data)">
              <span class="text-xs font-medium text-gray-500">{{ c.day }}</span>
            </div>
          }
        }
      </div>
    </div>
  `
})
export class MonthPageComponent implements OnInit, OnDestroy {
  private readonly hist = inject(WaveHistogramService);
  private readonly route = inject(ActivatedRoute);
  private sub?: Subscription;

  private readonly year = signal(0);
  private readonly month = signal(1);
  private readonly byDate = signal<Map<string, WaveActivityDay>>(new Map());

  readonly weekdays = WEEKDAYS;
  protected readonly rampColor = rampColor;
  protected readonly tooltip = tooltip;
  protected readonly emptyColor = EMPTY;
  protected readonly futureColor = '#f3f4f6';

  readonly cells = computed<DayCell[]>(() => {
    const year = this.year();
    const month = this.month();
    if (!year) {
      return [];
    }
    const map = this.byDate();
    const today = startOfDay(new Date());
    const first = new Date(year, month - 1, 1);
    const daysInMonth = new Date(year, month, 0).getDate();
    const out: DayCell[] = [];
    for (let pad = 0; pad < first.getDay(); pad++) {
      out.push({ day: 0, date: null, future: false, data: null }); // weekday padding before day 1
    }
    for (let day = 1; day <= daysInMonth; day++) {
      const date = new Date(year, month - 1, day);
      out.push({ day, date, future: date > today, data: map.get(toKey(date)) ?? null });
    }
    return out;
  });

  ngOnInit(): void {
    this.sub = this.route.paramMap.subscribe((pm) => {
      const { year = 0, month = 1 } = segmentsOf(pm);
      this.year.set(year);
      this.month.set(month);
      this.byDate.set(new Map()); // clear stale data while the new month loads
      const { from, to } = monthRange(this.year(), this.month());
      this.hist.histogram(from.toISOString(), to.toISOString(), 'day').subscribe((r) => {
        const next = new Map<string, WaveActivityDay>();
        for (const d of r.buckets) {
          const key = toKey(new Date(d.start));
          next.set(key, { date: key, count: d.count, completed: d.completed, faulted: d.faulted, running: d.running });
        }
        this.byDate.set(next);
      });
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  dayLink(day: number): unknown[] {
    return ['/waves', this.year(), this.month(), day];
  }

  futureTitle(date: Date): string {
    return `${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })} — upcoming`;
  }
}
