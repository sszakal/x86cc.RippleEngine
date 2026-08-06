import { Component, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

const COLLAPSE_KEY = 'ripple.sidebar.collapsed';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="flex h-full">
      <!-- Sidebar: toggles between expanded (w-64, labels) and compact (w-20, icons only) -->
      <aside
        class="flex shrink-0 flex-col border-r border-gray-200 bg-white py-6 transition-all duration-200"
        [class.w-64]="!collapsed()" [class.px-5]="!collapsed()"
        [class.w-20]="collapsed()" [class.px-3]="collapsed()">
        <div class="mb-8 flex items-center px-1"
             [class.justify-between]="!collapsed()" [class.justify-center]="collapsed()">
          @if (!collapsed()) {
            <div class="flex items-center gap-2">
              <div class="flex h-9 w-9 items-center justify-center rounded-lg bg-brand-500 font-bold text-white">R</div>
              <div class="text-lg font-semibold text-gray-900">Ripple</div>
            </div>
          }
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
        <nav class="space-y-1">
          <a routerLink="/metrics" routerLinkActive="bg-brand-50 text-brand-700"
             [title]="collapsed() ? 'Metrics' : ''"
             class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
             [class.justify-center]="collapsed()">
            <span class="text-base">📊</span>
            @if (!collapsed()) { <span>Metrics</span> }
          </a>
          <a routerLink="/waves" routerLinkActive="bg-brand-50 text-brand-700"
             [title]="collapsed() ? 'Waves' : ''"
             class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
             [class.justify-center]="collapsed()">
            <span class="text-base">🌊</span>
            @if (!collapsed()) { <span>Waves</span> }
          </a>
          <a routerLink="/settings" routerLinkActive="bg-brand-50 text-brand-700"
             [title]="collapsed() ? 'Settings' : ''"
             class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
             [class.justify-center]="collapsed()">
            <span class="text-base">⚙️</span>
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
  readonly now = new Date().toLocaleDateString();
  readonly collapsed = signal(localStorage.getItem(COLLAPSE_KEY) === '1');

  toggleSidebar(): void {
    this.collapsed.update((c) => !c);
    localStorage.setItem(COLLAPSE_KEY, this.collapsed() ? '1' : '0');
  }
}
