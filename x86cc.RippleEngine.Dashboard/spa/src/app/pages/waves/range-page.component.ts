import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { RippleApiService } from '../../core/ripple-api.service';
import { HistogramBucket, Wave } from '../../core/models';
import { WaveHistogramService } from './wave-histogram.service';
import { WaveTimelineComponent } from './wave-timeline.component';
import { bucketLabel, rampColor } from './wave-palette';
import { Gran, WAVE_TILE_LIMIT, WaveSegments, rangeFromSegments, segmentsOf } from './wave-time';

/**
 * /waves/:year/:month/:day/:hour[/:minute[/:second]] — a sub-day range. Shows the waves as a timeline when the
 * range is small enough (or is a single second); otherwise a finer bucket grid (minute/second) that drills
 * deeper. One component covers all three depths — the behavior is identical, only the granularity differs.
 * Opening a wave sets `?wave=<id>` (the shell renders the modal), preserving the current path as context.
 */
@Component({
  selector: 'app-range-page',
  standalone: true,
  imports: [DecimalPipe, RouterLink, WaveTimelineComponent],
  template: `
    <div>
      @if (mode() === 'grid') {
        <div class="mb-4 text-sm text-gray-500">{{ total() | number }} wave{{ total() === 1 ? '' : 's' }} · zoomed to {{ childGran() }}s</div>
      }

      @if (mode() === 'timeline') {
        <app-wave-timeline [waves]="waves()" [shown]="waves().length" [total]="total()" (open)="openWave($event)" />
      } @else if (mode() === 'grid') {
        <div class="flex flex-wrap gap-2">
          @for (b of buckets(); track b.start) {
            <a
              class="flex h-16 w-24 flex-col justify-between rounded-lg p-2 text-left ring-1 ring-inset ring-black/5 transition-transform hover:scale-[1.04]"
              [style.background]="rampColor(b.count, b.faulted, b.running)"
              [title]="bucketLabel(b, childGran()) + ' — ' + b.count + ' waves'" [routerLink]="deeperLink(b)">
              <span class="text-sm font-semibold text-gray-900">{{ bucketLabel(b, childGran()) }}</span>
              <span class="text-xs text-gray-700">{{ b.count | number }}</span>
            </a>
          } @empty {
            <div class="text-sm text-gray-400">No waves in this range.</div>
          }
        </div>
      } @else {
        <div class="text-sm text-gray-400">Loading…</div>
      }
    </div>
  `
})
export class RangePageComponent implements OnInit, OnDestroy {
  private readonly api = inject(RippleApiService);
  private readonly hist = inject(WaveHistogramService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private sub?: Subscription;

  private seg: WaveSegments = {};

  readonly mode = signal<'loading' | 'timeline' | 'grid'>('loading');
  readonly childGran = signal<Gran>('minute');
  readonly total = signal(0);
  readonly buckets = signal<HistogramBucket[]>([]);
  readonly waves = signal<Wave[]>([]);

  protected readonly rampColor = rampColor;
  protected readonly bucketLabel = bucketLabel;

  ngOnInit(): void {
    // This component is reused across the hour/minute/second routes, so react to paramMap (not just the snapshot).
    this.sub = this.route.paramMap.subscribe((pm) => {
      this.seg = segmentsOf(pm);
      this.load();
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  private load(): void {
    const { from, to, childGran, terminal } = rangeFromSegments(this.seg);
    this.childGran.set(childGran);
    this.mode.set('loading');
    this.buckets.set([]);
    this.waves.set([]);

    if (terminal) {
      this.showTimeline(from, to);
      return;
    }
    // Not terminal: the child histogram tells us the range total, which decides timeline vs a finer grid.
    // (childGran is 'minute' | 'second' here — 'waves' only arises on the terminal path handled above.)
    const grid = childGran as 'minute' | 'second';
    this.hist.histogram(from.toISOString(), to.toISOString(), grid).subscribe((r) => {
      const total = r.buckets.reduce((sum, b) => sum + b.count, 0);
      if (total <= WAVE_TILE_LIMIT) {
        this.showTimeline(from, to, total);
      } else {
        this.total.set(total);
        this.buckets.set(r.buckets);
        this.mode.set('grid');
      }
    });
  }

  private showTimeline(from: Date, to: Date, knownTotal?: number): void {
    this.api.getWaves({ from: from.toISOString(), to: to.toISOString() }, WAVE_TILE_LIMIT).subscribe((r) => {
      const waves = [...r.waves].sort((a, b) => +new Date(a.createdAt) - +new Date(b.createdAt));
      this.waves.set(waves);
      this.total.set(knownTotal ?? waves.length);
      this.mode.set('timeline');
    });
  }

  /** Route into the deeper level for a bucket (its minute, or its second). */
  deeperLink(b: HistogramBucket): unknown[] {
    const d = new Date(b.start);
    const { year, month, day, hour } = this.seg;
    if (this.childGran() === 'second') {
      return ['/waves', year, month, day, hour, this.seg.minute, d.getSeconds()];
    }
    return ['/waves', year, month, day, hour, d.getMinutes()];
  }

  openWave(w: Wave): void {
    // Add ?wave=<id> while keeping the current calendar path exactly (the shell renders the modal).
    const tree = this.router.parseUrl(this.router.url);
    tree.queryParams = { ...tree.queryParams, wave: w.id };
    this.router.navigateByUrl(tree);
  }
}
