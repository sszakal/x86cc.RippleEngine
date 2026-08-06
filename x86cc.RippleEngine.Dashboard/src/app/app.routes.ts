import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'metrics' },
  {
    path: 'metrics',
    loadComponent: () => import('./pages/metrics.component').then((m) => m.MetricsComponent)
  },
  {
    path: 'waves',
    loadComponent: () => import('./pages/waves/waves-shell.component').then((m) => m.WavesShellComponent),
    children: [
      { path: '', loadComponent: () => import('./pages/waves/years-page.component').then((m) => m.YearsPageComponent) },
      { path: ':year', loadComponent: () => import('./pages/waves/year-page.component').then((m) => m.YearPageComponent) },
      { path: ':year/:month', loadComponent: () => import('./pages/waves/month-page.component').then((m) => m.MonthPageComponent) },
      { path: ':year/:month/:day', loadComponent: () => import('./pages/waves/day-page.component').then((m) => m.DayPageComponent) },
      { path: ':year/:month/:day/:hour', loadComponent: () => import('./pages/waves/range-page.component').then((m) => m.RangePageComponent) },
      { path: ':year/:month/:day/:hour/:minute', loadComponent: () => import('./pages/waves/range-page.component').then((m) => m.RangePageComponent) },
      { path: ':year/:month/:day/:hour/:minute/:second', loadComponent: () => import('./pages/waves/range-page.component').then((m) => m.RangePageComponent) }
    ]
  },
  {
    path: 'settings',
    loadComponent: () => import('./pages/settings.component').then((m) => m.SettingsComponent)
  },
  { path: '**', redirectTo: 'waves' }
];
