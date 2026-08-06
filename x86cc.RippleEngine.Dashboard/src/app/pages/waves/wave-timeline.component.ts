import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { Wave } from '../../core/models';
import { statusClass, waveDot, waveTint } from './wave-palette';

/**
 * The terminal 'waves' view: individual waves as a time-ordered timeline — a status dot on a shared track
 * plus one tile per wave. Purely presentational; the host supplies the (already time-sorted) waves and the
 * shown/total counts, and listens for `open`.
 */
@Component({
  selector: 'app-wave-timeline',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  template: `
    <ol class="mt-1">
      @for (w of waves; track w.id; let last = $last) {
        <li class="flex items-stretch gap-3" [class.pb-3]="!last">
          <div class="w-20 shrink-0 self-center text-right font-mono text-xs text-gray-500">{{ w.createdAt | date: 'HH:mm:ss' }}</div>
          <div class="relative flex w-4 shrink-0 items-center justify-center">
            <div class="absolute inset-y-0 w-px bg-gray-200"></div>
            <div class="relative z-10 h-3 w-3 rounded-full ring-2 ring-white" [style.background]="waveDot(w.status)"></div>
          </div>
          <button
            class="flex flex-1 items-center justify-between gap-3 rounded-xl p-3 text-left ring-1 ring-inset ring-black/5 transition-transform hover:scale-[1.01]"
            [style.background]="waveTint(w.status)" (click)="open.emit(w)">
            <span class="line-clamp-1 min-w-0 text-sm font-semibold text-gray-900">{{ w.name }}</span>
            <span class="flex shrink-0 items-center gap-3 text-xs text-gray-600">
              <span>{{ w.rippleCount | number }} ripples</span>
              <span class="badge" [class]="statusClass(w.status)">{{ w.status }}</span>
            </span>
          </button>
        </li>
      } @empty {
        <li class="text-sm text-gray-400">No waves in this range.</li>
      }
    </ol>
    @if (shown < total) {
      <p class="mt-3 text-xs text-amber-600">Showing {{ shown }} of {{ total | number }} — zoom in for the rest.</p>
    }
  `
})
export class WaveTimelineComponent {
  @Input() waves: Wave[] = [];
  @Input() shown = 0;
  @Input() total = 0;
  @Output() open = new EventEmitter<Wave>();

  protected readonly waveTint = waveTint;
  protected readonly waveDot = waveDot;
  protected readonly statusClass = statusClass;
}
