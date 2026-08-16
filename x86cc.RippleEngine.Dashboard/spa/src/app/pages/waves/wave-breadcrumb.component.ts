import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { filter } from 'rxjs/operators';
import { WaveSegments, segmentsOfUrl } from './wave-time';

interface Crumb {
  label: string;
  link: unknown[];
  active: boolean;
  home?: boolean; // the leading root segment (rendered as a home icon → /waves)
  bg: string; // the segment's single solid colour
  fg: string; // segment text colour
}

// Powerline palette: parent segments alternate two close greys so the arrow between them reads; the current
// level is brand-highlighted. Each segment is ONE solid colour — the arrow is its own pointed right edge.
const BRAND = '#465fff';
const SEG_FG = '#334155';
const SHADE_A = '#e5e7eb';
const SHADE_B = '#c3cad4';

/**
 * The drill path as a powerline (powerlevel10k-style) prompt. Each segment is a single solid colour shaped with
 * a pointed right edge (clip-path) and overlapped onto the next segment — so the arrow is genuinely part of its
 * own segment and never blends two colours. Built from the /waves URL; each segment links up the tree. Empty at
 * the root (the header then shows only the stat pills).
 */
@Component({
  selector: 'app-wave-breadcrumb',
  standalone: true,
  imports: [RouterLink],
  styles: [
    `
      .pl { display: inline-flex; align-items: stretch; }
      /*
       * One solid-colour banner per segment: the right edge tapers to a point, and each segment slides 9px left
       * (margin) UNDER the previous one, whose higher z-index keeps its point on top. So segment A's point (colour
       * A) sits over segment B (colour B) — a crisp single-colour arrow, no two-tone separator strip.
       */
      .pl-seg {
        position: relative;
        display: inline-flex; align-items: center;
        height: 22px; padding: 0 16px; margin-left: -9px;
        font-size: 12px; line-height: 1; white-space: nowrap; text-decoration: none;
        clip-path: polygon(0 0, calc(100% - 9px) 0, 100% 50%, calc(100% - 9px) 100%, 0 100%);
      }
      .pl-seg.pl-first { margin-left: 0; }
      .pl-seg:hover { filter: brightness(0.96); }
      .pl-home { width: 12px; height: 12px; }
    `
  ],
  template: `
    @if (crumbs().length) {
      <div class="pl">
        @for (c of crumbs(); track $index) {
          <a [routerLink]="c.link" class="pl-seg" [class.pl-first]="$first"
             [style.background]="c.bg" [style.color]="c.fg" [style.zIndex]="crumbs().length - $index">
            @if (c.home) {
              <svg class="pl-home" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
                <path d="M10.707 2.293a1 1 0 0 0-1.414 0l-7 7A1 1 0 0 0 3 11h1v6a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-3h2v3a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-6h1a1 1 0 0 0 .707-1.707l-7-7Z" />
              </svg>
            } @else {
              {{ c.label }}
            }
          </a>
        }
      </div>
    }
  `
})
export class WaveBreadcrumbComponent implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private sub?: Subscription;
  private readonly url = signal(this.router.url);

  readonly crumbs = computed<Crumb[]>(() => buildCrumbs(segmentsOfUrl(this.url())));

  ngOnInit(): void {
    this.sub = this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((e) => this.url.set(e.urlAfterRedirects));
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }
}

function buildCrumbs(s: WaveSegments): Crumb[] {
  // The path always starts with the home segment; at the root that's the whole path.
  const base = (label: string, link: unknown[], home = false): Crumb => ({ label, link, home, active: false, bg: '', fg: '' });
  const out: Crumb[] = [base('home', ['/waves'], true)];
  if (s.year != null) {
    out.push(base(`${s.year}`, ['/waves', s.year]));
    if (s.month != null) {
      out.push(base(monthName(s.year, s.month), ['/waves', s.year, s.month]));
      if (s.day != null) {
        out.push(base(dayOrdinal(s.day), ['/waves', s.year, s.month, s.day]));
        if (s.hour != null) {
          out.push(base(clock(s.hour), ['/waves', s.year, s.month, s.day, s.hour]));
          if (s.minute != null) {
            out.push(base(clock(s.hour, s.minute), ['/waves', s.year, s.month, s.day, s.hour, s.minute]));
            if (s.second != null) {
              out.push(base(clock(s.hour, s.minute, s.second), ['/waves', s.year, s.month, s.day, s.hour, s.minute, s.second]));
            }
          }
        }
      }
    }
  }

  // One solid colour per segment: parents alternate two greys (so each arrow reads), current level = brand.
  const last = out.length - 1;
  out.forEach((c, i) => {
    c.active = i === last;
    c.bg = c.active ? BRAND : i % 2 === 0 ? SHADE_A : SHADE_B;
    c.fg = c.active ? '#ffffff' : SEG_FG;
  });
  return out;
}

/** Full month name, e.g. "August". */
function monthName(year: number, month: number): string {
  return new Date(year, month - 1, 1).toLocaleDateString(undefined, { month: 'long' });
}

/** The day number with its ordinal suffix, e.g. "5th". */
function dayOrdinal(day: number): string {
  return `${day}${ordinal(day)}`;
}

function ordinal(n: number): string {
  if (n % 100 >= 11 && n % 100 <= 13) {
    return 'th';
  }
  switch (n % 10) {
    case 1: return 'st';
    case 2: return 'nd';
    case 3: return 'rd';
    default: return 'th';
  }
}

function clock(h: number, m?: number, s?: number): string {
  const pad = (n: number) => `${n}`.padStart(2, '0');
  let out = `${pad(h)}:${pad(m ?? 0)}`;
  if (s != null) {
    out += `:${pad(s)}`;
  }
  return out;
}
