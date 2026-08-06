import { DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HistogramBucket } from '../../core/models';
import { WaveHistogramService } from './wave-histogram.service';
import { rampColor, yearOf } from './wave-palette';

/** /waves — the years overview: one tile per year that has waves, linking into that year's calendar. */
@Component({
  selector: 'app-years-page',
  standalone: true,
  imports: [DecimalPipe, RouterLink],
  template: `
    <div>
      <div class="flex flex-wrap gap-3">
        @for (b of buckets(); track b.start) {
          <a
            class="flex h-20 w-28 flex-col justify-between rounded-xl p-3 text-left ring-1 ring-inset ring-black/5 transition-transform hover:scale-[1.04]"
            [style.background]="rampColor(b.count, b.faulted, b.running)" [routerLink]="['/waves', yearOf(b)]">
            <span class="text-lg font-semibold text-gray-900">{{ yearOf(b) }}</span>
            <span class="text-xs text-gray-700">{{ b.count | number }} wave{{ b.count === 1 ? '' : 's' }}</span>
          </a>
        } @empty {
          <div class="text-sm text-gray-400">No waves yet.</div>
        }
      </div>
    </div>
  `
})
export class YearsPageComponent implements OnInit {
  private readonly hist = inject(WaveHistogramService);
  readonly buckets = signal<HistogramBucket[]>([]);

  protected readonly rampColor = rampColor;
  protected readonly yearOf = yearOf;

  ngOnInit(): void {
    const from = new Date(2000, 0, 1).toISOString();
    const to = new Date(new Date().getFullYear() + 1, 0, 1).toISOString();
    this.hist.histogram(from, to, 'year').subscribe((r) => this.buckets.set(r.buckets));
  }
}
