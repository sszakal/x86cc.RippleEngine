import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { WaveActivityDay } from '../../core/models';
import { WaveHistogramService } from './wave-histogram.service';
import { EMPTY, crit, good, legendRamp, rampColor, tileColor, tooltip, warn } from './wave-palette';
import { MONTHS, startOfDay, toKey, yearRange } from './wave-time';

interface Cell {
  date: Date;
  key: string;
  day: WaveActivityDay | null;
  future: boolean;
}

/** A mini month calendar: 7-column cells (null = weekday padding before day 1). */
interface MonthBlock {
  key: string;
  label: string;
  cells: (Cell | null)[];
}

/** /waves/:year — the twelve mini-month calendars; each day with waves links into its 24-hour grid. */
@Component({
  selector: 'app-year-page',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div>
      <div class="flex flex-wrap items-start gap-x-6 gap-y-5">
        @for (block of months(); track block.key; let mi = $index) {
          <div class="flex flex-col gap-1">
            <a [routerLink]="['/waves', year(), mi + 1]"
               class="text-[11px] font-medium text-gray-500 hover:text-gray-800">{{ block.label }}</a>
            <div class="grid grid-cols-7 gap-[3px]">
              @for (cell of block.cells; track $index) {
                @if (!cell) {
                  <!-- weekday padding before day 1 — not a day -->
                  <div class="h-[11px] w-[11px]"></div>
                } @else if (cell.future) {
                  <!-- a real day, but in the future: shown as a disabled tile (no waves can fall there yet) -->
                  <div class="h-[11px] w-[11px] cursor-default rounded-[2px] ring-1 ring-inset ring-black/5"
                       [style.background]="futureColor" [title]="futureTitle(cell.date)"></div>
                } @else if (cell.day && cell.day.count > 0) {
                  <a class="h-[11px] w-[11px] cursor-pointer rounded-[2px] ring-1 ring-inset ring-black/5 transition-transform hover:scale-125"
                     [style.background]="tileColor(cell.day)" [title]="tooltip(cell.date, cell.day)"
                     [routerLink]="dayLink(cell.date)"></a>
                } @else {
                  <div class="h-[11px] w-[11px] rounded-[2px] ring-1 ring-inset ring-black/5"
                       [style.background]="emptyColor" [title]="tooltip(cell.date, cell.day)"></div>
                }
              }
            </div>
          </div>
        }
      </div>
      <div class="mt-6 flex flex-wrap items-center gap-x-6 gap-y-2 text-[11px] text-gray-500">
        <div class="flex items-center gap-1">
          <span>Less</span>
          @for (c of legendRamp; track $index) { <span class="h-[11px] w-[11px] rounded-[2px]" [style.background]="c"></span> }
          <span>More</span>
        </div>
        <div class="flex items-center gap-3">
          <span class="flex items-center gap-1"><span class="h-[11px] w-[11px] rounded-[2px]" [style.background]="good"></span> all completed</span>
          <span class="flex items-center gap-1"><span class="h-[11px] w-[11px] rounded-[2px]" [style.background]="warn"></span> active</span>
          <span class="flex items-center gap-1"><span class="h-[11px] w-[11px] rounded-[2px]" [style.background]="crit"></span> has faults</span>
        </div>
      </div>
    </div>
  `
})
export class YearPageComponent implements OnInit, OnDestroy {
  private readonly hist = inject(WaveHistogramService);
  private readonly route = inject(ActivatedRoute);
  private sub?: Subscription;

  readonly year = signal(0);
  private readonly byDate = signal<Map<string, WaveActivityDay>>(new Map());

  // Legend swatches + palette for the template.
  protected readonly legendRamp = legendRamp;
  protected readonly good = good;
  protected readonly warn = warn;
  protected readonly crit = crit;
  protected readonly emptyColor = EMPTY;
  protected readonly futureColor = '#f3f4f6'; // lighter than EMPTY — reads as a disabled/upcoming day
  protected readonly tileColor = tileColor;
  protected readonly tooltip = tooltip;

  futureTitle(date: Date): string {
    return `${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })} — upcoming`;
  }

  /** Twelve mini-month calendars (Jan–Dec) of the year, each a weekday-aligned 7-column grid. */
  readonly months = computed<MonthBlock[]>(() => {
    const year = this.year();
    if (!year) {
      return [];
    }
    const map = this.byDate();
    const today = startOfDay(new Date());
    const blocks: MonthBlock[] = [];
    for (let month = 0; month < 12; month++) {
      const first = new Date(year, month, 1);
      const daysInMonth = new Date(year, month + 1, 0).getDate();
      const cells: (Cell | null)[] = [];
      for (let pad = 0; pad < first.getDay(); pad++) {
        cells.push(null); // leading blanks so day 1 lands under its weekday (Sunday-first)
      }
      for (let day = 1; day <= daysInMonth; day++) {
        const date = new Date(year, month, day);
        const key = toKey(date);
        const future = date > today;
        cells.push({ date, key, day: future ? null : map.get(key) ?? null, future });
      }
      blocks.push({ key: `${year}-${month}`, label: MONTHS[month], cells });
    }
    return blocks;
  });

  ngOnInit(): void {
    this.sub = this.route.paramMap.subscribe((pm) => {
      const year = Number(pm.get('year'));
      this.year.set(year);
      this.byDate.set(new Map());
      const { from, to } = yearRange(year);
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

  dayLink(date: Date): unknown[] {
    return ['/waves', date.getFullYear(), date.getMonth() + 1, date.getDate()];
  }
}
