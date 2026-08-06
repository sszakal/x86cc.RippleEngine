// The status-color system + label helpers shared by the wave tiles, calendar, hour grid and timeline.
// Sequential ramps per status (light→dark by count); status hue carries meaning, tooltip/legend are the
// non-color encoding. Anchors are the dataviz status palette (good/warning/critical), validated CVD-safe.

import { HistogramBucket, WaveActivityDay, WaveStatus } from '../../core/models';
import { Gran } from './wave-time';

export const EMPTY = '#ebedf0';
export const GOOD = ['#c6ecc6', '#7ccf7c', '#2fb02f', '#0a8a0a']; // all completed
export const WARN = ['#fde7bb', '#fbcf67', '#f6b429', '#dd9406']; // some active / pending
export const CRIT = ['#f3c3c3', '#e28a8a', '#d55151', '#c22a2a']; // some faulted

// Legend swatches (year calendar).
export const legendRamp = [EMPTY, ...GOOD];
export const good = GOOD[3];
export const warn = WARN[3];
export const crit = CRIT[3];

/** Tile fill for a count, escalated to the warn/crit ramp when the range has active/faulted waves. */
export function rampColor(count: number, faulted: number, running: number): string {
  if (count === 0) {
    return EMPTY;
  }
  const level = count <= 2 ? 0 : count <= 5 ? 1 : count <= 10 ? 2 : 3;
  const ramp = faulted > 0 ? CRIT : running > 0 ? WARN : GOOD;
  return ramp[level];
}

export function tileColor(day: WaveActivityDay | null): string {
  return day ? rampColor(day.count, day.faulted, day.running) : EMPTY;
}

/** Light tint for a wave tile background. */
export function waveTint(status: WaveStatus): string {
  switch (status) {
    case 'Completed': return GOOD[0];
    case 'Faulted': return CRIT[0];
    default: return WARN[0];
  }
}

/** Saturated status colour for the timeline dot (the tile itself uses the light waveTint). */
export function waveDot(status: WaveStatus): string {
  switch (status) {
    case 'Completed': return GOOD[3];
    case 'Faulted': return CRIT[3];
    default: return WARN[3];
  }
}

export function statusClass(status: WaveStatus): string {
  switch (status) {
    case 'Completed': return 'bg-emerald-100 text-emerald-700';
    case 'Faulted': return 'bg-rose-100 text-rose-700';
    default: return 'bg-amber-100 text-amber-700';
  }
}

export function hourLabel(hour: number): string {
  return `${`${hour}`.padStart(2, '0')}:00`;
}

/** Clock label for a minute/second bucket tile. */
export function bucketLabel(b: HistogramBucket, gran: Gran): string {
  const d = new Date(b.start);
  if (gran === 'second') {
    return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

export function tooltip(date: Date, day: WaveActivityDay | null): string {
  const label = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  if (!day || day.count === 0) {
    return `${label} — no waves`;
  }
  const parts: string[] = [];
  if (day.completed) parts.push(`${day.completed} completed`);
  if (day.running) parts.push(`${day.running} active`);
  if (day.faulted) parts.push(`${day.faulted} faulted`);
  return `${label} — ${day.count} wave${day.count === 1 ? '' : 's'} (${parts.join(', ')})`;
}

export function yearOf(b: HistogramBucket): number {
  return new Date(b.start).getFullYear();
}
