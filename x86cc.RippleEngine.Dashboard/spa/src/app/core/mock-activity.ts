import { WaveActivityDay, WaveActivityResponse } from './models';

/**
 * Generates a plausible year of wave activity for the contribution heatmap. Used as the fallback when the
 * API is unavailable, so the widget renders populated during `ng serve` with no backend. Counts are skewed
 * low with clusters of busy days and the occasional fault/still-active day.
 */
export function mockActivity(days = 365): WaveActivityResponse {
  const today = new Date();
  const out: WaveActivityDay[] = [];

  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(today);
    d.setDate(today.getDate() - i);

    // ~55% of days have no waves; the rest skew low with an occasional busy spike.
    const roll = Math.random();
    let count = 0;
    if (roll > 0.45) {
      count = 1 + Math.floor(Math.random() * 3);
      if (Math.random() > 0.85) count += Math.floor(Math.random() * 12); // occasional heavy day
    }

    let faulted = 0;
    let running = 0;
    if (count > 0) {
      if (Math.random() > 0.88) faulted = 1 + Math.floor(Math.random() * Math.min(count, 3));
      if (i <= 1 && Math.random() > 0.4) running = 1 + Math.floor(Math.random() * Math.max(1, count - faulted));
    }
    const completed = Math.max(0, count - faulted - running);

    out.push({ date: toKey(d), count, completed, faulted, running });
  }

  return { days: out };
}

/** YYYY-MM-DD in local time (matches how the heatmap keys days). */
export function toKey(d: Date): string {
  const m = `${d.getMonth() + 1}`.padStart(2, '0');
  const day = `${d.getDate()}`.padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}
