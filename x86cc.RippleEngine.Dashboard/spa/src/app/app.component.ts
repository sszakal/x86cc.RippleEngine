import { Component, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { RippleLogoComponent } from './core/ripple-logo.component';

const COLLAPSE_KEY = 'ripple.sidebar.collapsed';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, RippleLogoComponent],
  template: `
    <div class="flex h-full">
      <!-- Sidebar: toggles between expanded (w-64, labels) and compact (w-20, icons only) -->
      <aside
        class="flex shrink-0 flex-col border-r border-gray-200 bg-white py-6 transition-all duration-200"
        [class.w-64]="!collapsed()" [class.px-5]="!collapsed()"
        [class.w-20]="collapsed()" [class.px-3]="collapsed()">
        <!--
          The mark stays in the collapsed rail (only the wordmark drops) so the rail keeps an identity. w-20
          can't fit the 36px mark and the 32px button side by side, so the header stacks into a column at that
          width.
        -->
        <div class="mb-8 flex items-center px-1"
             [class.justify-between]="!collapsed()"
             [class.flex-col]="collapsed()" [class.gap-3]="collapsed()">
          <div class="flex items-center gap-2">
            <app-ripple-logo />
            @if (!collapsed()) {
              <div class="text-lg font-semibold text-gray-900">Ripple</div>
            }
          </div>
          <button type="button" (click)="toggleSidebar()"
                  [attr.aria-label]="collapsed() ? 'Expand sidebar' : 'Collapse sidebar'"
                  [title]="collapsed() ? 'Expand' : 'Collapse'"
                  class="flex h-8 w-8 items-center justify-center rounded-lg text-gray-500 hover:bg-gray-100">
            <svg class="h-4 w-4 transition-transform" [class.rotate-180]="collapsed()"
                 viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path fill-rule="evenodd" d="M12.79 5.23a.75.75 0 0 1 0 1.06L9.06 10l3.73 3.71a.75.75 0 1 1-1.06 1.06l-4.25-4.24a.75.75 0 0 1 0-1.06l4.25-4.24a.75.75 0 0 1 1.06 0Z" clip-rule="evenodd" />
            </svg>
          </button>
        </div>
        <!--
          Nav icons are hand-drawn to the same rules as the logo (see ripple-logo.component.ts): 24px grid,
          1.6 stroke, round caps, and one filled "drop" accent each. They're stroked in currentColor, so unlike
          the emoji they replaced they pick up the active/hover text colour.
        -->
        <nav class="space-y-1">
          <a routerLink="/metrics" routerLinkActive="bg-brand-50 text-brand-700"
             [title]="collapsed() ? 'Metrics' : ''"
             class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
             [class.justify-center]="collapsed()">
            <!-- Metrics: a trend line, its latest point called out as the drop. -->
            <svg class="h-5 w-5 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                 stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M3.5 16.5 9 11l4 4 7.5-8.5" />
              <circle cx="20.5" cy="6.5" r="2.1" fill="currentColor" stroke="none" />
            </svg>
            @if (!collapsed()) { <span>Metrics</span> }
          </a>
          <a routerLink="/waves" routerLinkActive="bg-brand-50 text-brand-700"
             [title]="collapsed() ? 'Waves' : ''"
             class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
             [class.justify-center]="collapsed()">
            <!-- Waves: three crests, the outer two faded — the logo's rings fade outward the same way. -->
            <svg class="h-5 w-5 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                 stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <!-- Identical phase on every row, so the crests stay parallel and can't collide. -->
              <path d="M3 7q3-4 6 0t6 0t6 0" opacity=".45" />
              <path d="M3 12q3-4 6 0t6 0t6 0" />
              <path d="M3 17q3-4 6 0t6 0t6 0" opacity=".45" />
            </svg>
            @if (!collapsed()) { <span>Waves</span> }
          </a>
          <a routerLink="/settings" routerLinkActive="bg-brand-50 text-brand-700"
             [title]="collapsed() ? 'Settings' : ''"
             class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
             [class.justify-center]="collapsed()">
            <!-- Settings: sliders rather than a gear — thin rails suit the stroke weight, and the page really
                 is a rack of per-type knobs (batch size, gap, max attempts). -->
            <svg class="h-5 w-5 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                 stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M4 8.5h16M4 15.5h16" />
              <circle cx="9" cy="8.5" r="2.4" fill="currentColor" stroke="none" />
              <circle cx="16" cy="15.5" r="2.4" fill="currentColor" stroke="none" />
            </svg>
            @if (!collapsed()) { <span>Settings</span> }
          </a>
        </nav>
      </aside>

      <!-- Main -->
      <div class="flex min-w-0 flex-1 flex-col">
        <main class="flex-1 overflow-y-auto p-6">
          <router-outlet />
        </main>
      </div>
    </div>
  `
})
export class AppComponent {
  readonly collapsed = signal(localStorage.getItem(COLLAPSE_KEY) === '1');

  toggleSidebar(): void {
    this.collapsed.update((c) => !c);
    localStorage.setItem(COLLAPSE_KEY, this.collapsed() ? '1' : '0');
  }
}
