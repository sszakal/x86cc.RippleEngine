/**
 * A compact, human duration for an estimate: `45s`, `3m 4s`, `2h 15m`, `3d 4h`. Floors each unit so a value
 * never reads longer than it is (59.6s is `59s`, not `60s` or `1m 0s`). Sub-second values read as `<1s`.
 */
export function formatDuration(seconds: number): string {
  if (seconds < 1) {
    return '<1s';
  }
  const s = Math.floor(seconds);
  if (s < 60) {
    return `${s}s`;
  }
  if (s < 3600) {
    return `${Math.floor(s / 60)}m ${s % 60}s`;
  }
  if (s < 86400) {
    return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
  }
  return `${Math.floor(s / 86400)}d ${Math.floor((s % 86400) / 3600)}h`;
}
