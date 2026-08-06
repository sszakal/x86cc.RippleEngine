import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { Subscription } from 'rxjs';
import { filter } from 'rxjs/operators';
import { WaveStats } from '../../core/models';
import { WaveHistogramService } from './wave-histogram.service';
import { segmentsOfUrl, statsRange } from './wave-time';
import { WaveBreadcrumbComponent } from './wave-breadcrumb.component';
import { WavePopupComponent } from './wave-popup.component';

/**
 * The /waves frame: the breadcrumb path + zoom-context stat pills over the routed zoom pages
 * (`<router-outlet>`). The pills recompute for whatever range the URL is on (all-time at the root). The
 * wave-detail modal is hosted here and driven by the `?wave=<id>` query param, so a wave is addressable by id
 * alone (`/waves?wave=<id>`) with the calendar path being optional context.
 */
@Component({
  selector: 'app-waves-shell',
  standalone: true,
  imports: [RouterOutlet, WaveBreadcrumbComponent, WavePopupComponent],
  styles: [
    `
      /*
       * The stats as a right-side powerline (powerlevel10k RPROMPT): mirror of the breadcrumb — each segment is
       * one solid colour with a pointed LEFT edge and overlaps the segment to its left (higher z-index left→right),
       * so the arrows point the opposite way. The leftmost segment's point heads into the page.
       */
      .rpl { display: inline-flex; }
      .rpl-seg {
        position: relative;
        display: inline-flex; align-items: center;
        height: 22px; padding: 0 14px; margin-left: -9px;
        font-size: 12px; line-height: 1; white-space: nowrap; color: #fff; font-weight: 500;
        clip-path: polygon(9px 0, 100% 0, 100% 100%, 9px 100%, 0 50%);
      }
      .rpl-seg:first-child { margin-left: 0; }
      .rpl-seg:nth-child(1) { z-index: 1; }
      .rpl-seg:nth-child(2) { z-index: 2; }
      .rpl-seg:nth-child(3) { z-index: 3; }
      .rpl-seg:nth-child(4) { z-index: 4; }
    `
  ],
  template: `
    <!-- One header line: the drill path on the left, the stats as a right-side powerline on the right. -->
    <div class="mb-6 flex flex-wrap items-center justify-between gap-3">
      <app-wave-breadcrumb />
      <div class="rpl">
        <span class="rpl-seg" style="background: #16a34a">{{ stats().completed }} completed</span>
        <span class="rpl-seg" style="background: #d97706">{{ stats().active }} active</span>
        <span class="rpl-seg" style="background: #e11d48">{{ stats().faulted }} faulted</span>
        <span class="rpl-seg" style="background: #475569">{{ stats().total }} total</span>
      </div>
    </div>

    <router-outlet />

    @if (waveId(); as id) {
      <app-wave-popup [waveId]="id" (closed)="closeWave()" />
    }
  `
})
export class WavesShellComponent implements OnInit, OnDestroy {
  private readonly hist = inject(WaveHistogramService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private sub?: Subscription;

  readonly stats = signal<WaveStats>({ total: 0, active: 0, completed: 0, faulted: 0 });
  readonly waveId = signal<string | null>(null);
  private lastPath = '';

  ngOnInit(): void {
    // The pills reflect the CURRENT zoom: recompute them from the range in the URL on each navigation.
    this.updateStats(this.router.url);
    const nav = this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((e) => this.updateStats(e.urlAfterRedirects));

    // The open wave lives in the URL (?wave=<id>) so it survives refresh and is shareable by id alone.
    this.sub = this.route.queryParamMap.subscribe((pm) => this.waveId.set(pm.get('wave')));
    this.sub.add(nav);
  }

  /** Sum the histogram over the current zoom's range into the header pills (all-time = system total at the root). */
  private updateStats(url: string): void {
    const path = url.split(/[?#]/)[0];
    if (path === this.lastPath) {
      return; // path unchanged (e.g. only ?wave toggled) — the counts can't have changed
    }
    this.lastPath = path;
    const { from, to, bucket } = statsRange(segmentsOfUrl(url));
    this.hist.histogram(from.toISOString(), to.toISOString(), bucket).subscribe((r) => {
      this.stats.set(
        r.buckets.reduce(
          (a, b) => ({ total: a.total + b.count, completed: a.completed + b.completed, faulted: a.faulted + b.faulted, active: a.active + b.running }),
          { total: 0, completed: 0, faulted: 0, active: 0 } as WaveStats
        )
      );
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  closeWave(): void {
    // Drop ?wave but keep whatever calendar path is active (this route is the parent, so a relative
    // navigate([]) would collapse to /waves and lose the drill depth — edit the URL tree instead).
    const tree = this.router.parseUrl(this.router.url);
    delete tree.queryParams['wave'];
    this.router.navigateByUrl(tree);
  }
}
