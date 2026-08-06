// Time helpers shared by the routed wave pages. The drill is calendar-aligned, so a URL like
// /waves/2026/8/4/14 maps to a [from, to) range; these functions do that mapping (in LOCAL time, matching
// the timezone the histogram API already buckets on).

import { ParamMap } from '@angular/router';

// A time granularity below the day. 'waves' is terminal — individual waves on a timeline (no more subdividing).
export type Gran = 'hour' | 'minute' | 'second' | 'waves';

// A range with at most this many waves is shown as a wave timeline; more ⇒ subdivide into a finer bucket grid.
export const WAVE_TILE_LIMIT = 60;

export const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** The calendar coordinates parsed from the /waves/:year/:month/:day/:hour/:minute/:second path (1-based m/d). */
export interface WaveSegments {
  year?: number;
  month?: number;
  day?: number;
  hour?: number;
  minute?: number;
  second?: number;
}

/** A sub-day range plus what a tile in it drills into. */
export interface RangeInfo {
  from: Date;
  to: Date;
  childGran: Gran; // the granularity of the grid shown when the range is too busy for a timeline
  terminal: boolean; // a single second — nothing finer to subdivide into
}

function num(map: ParamMap, key: string): number | undefined {
  const raw = map.get(key);
  if (raw === null) {
    return undefined;
  }
  const n = Number(raw);
  return Number.isFinite(n) ? n : undefined;
}

/** Read the calendar coordinates from a route's paramMap. */
export function segmentsOf(map: ParamMap): WaveSegments {
  return {
    year: num(map, 'year'),
    month: num(map, 'month'),
    day: num(map, 'day'),
    hour: num(map, 'hour'),
    minute: num(map, 'minute'),
    second: num(map, 'second')
  };
}

/** Parse the calendar coordinates out of a URL path (used by the breadcrumb, which sees the whole chain). */
export function segmentsOfUrl(url: string): WaveSegments {
  const path = url.split(/[?#]/)[0];
  const parts = path.split('/').filter(Boolean); // ['waves','2026','8','4','14']
  const i = parts.indexOf('waves');
  const seg = i < 0 ? [] : parts.slice(i + 1).map((p) => Number(p));
  const [year, month, day, hour, minute, second] = seg;
  const ok = (n: number | undefined) => (Number.isFinite(n) ? (n as number) : undefined);
  return { year: ok(year), month: ok(month), day: ok(day), hour: ok(hour), minute: ok(minute), second: ok(second) };
}

export function yearRange(year: number): { from: Date; to: Date } {
  return { from: new Date(year, 0, 1), to: new Date(year + 1, 0, 1) };
}

export function monthRange(year: number, month: number): { from: Date; to: Date } {
  return { from: new Date(year, month - 1, 1), to: new Date(year, month, 1) };
}

export function dayRange(year: number, month: number, day: number): { from: Date; to: Date } {
  const from = new Date(year, month - 1, day);
  return { from, to: addDays(from, 1) };
}

/**
 * The [from, to) range for a sub-day page (hour / minute / second), from its calendar coordinates, plus the
 * granularity a busy range subdivides into. `hour` is always present on these routes.
 */
export function rangeFromSegments(s: WaveSegments): RangeInfo {
  const { year = 1970, month = 1, day = 1, hour = 0, minute, second } = s;
  if (second != null) {
    const from = new Date(year, month - 1, day, hour, minute ?? 0, second);
    return { from, to: new Date(from.getTime() + 1_000), childGran: 'waves', terminal: true };
  }
  if (minute != null) {
    const from = new Date(year, month - 1, day, hour, minute);
    return { from, to: new Date(from.getTime() + 60_000), childGran: 'second', terminal: false };
  }
  const from = new Date(year, month - 1, day, hour);
  return { from, to: new Date(from.getTime() + 3_600_000), childGran: 'minute', terminal: false };
}

/**
 * The [from, to) range for the CURRENT zoom (from its URL segments) plus a histogram granularity that covers it,
 * used to sum per-bucket stats for the header pills. At the root this is all-time (the system total).
 */
export function statsRange(s: WaveSegments): { from: Date; to: Date; bucket: 'year' | 'day' | 'hour' | 'minute' | 'second' } {
  if (s.year == null) {
    return { from: new Date(2000, 0, 1), to: new Date(new Date().getFullYear() + 1, 0, 1), bucket: 'year' };
  }
  if (s.month == null) {
    return { ...yearRange(s.year), bucket: 'day' };
  }
  if (s.day == null) {
    return { ...monthRange(s.year, s.month), bucket: 'day' };
  }
  if (s.hour == null) {
    return { ...dayRange(s.year, s.month, s.day), bucket: 'hour' };
  }
  const r = rangeFromSegments(s);
  return { from: r.from, to: r.to, bucket: r.terminal ? 'second' : (r.childGran as 'minute' | 'second') };
}

export function startOfDay(d: Date): Date {
  const x = new Date(d);
  x.setHours(0, 0, 0, 0);
  return x;
}

export function addDays(d: Date, n: number): Date {
  const x = new Date(d);
  x.setDate(x.getDate() + n);
  return x;
}

/** YYYY-MM-DD in local time (matches how the calendar keys days). */
export function toKey(d: Date): string {
  const m = `${d.getMonth() + 1}`.padStart(2, '0');
  const day = `${d.getDate()}`.padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}
