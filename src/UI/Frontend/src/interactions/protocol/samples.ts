// Bounded time samples with interpolation and shared velocity estimation.

export interface Timed { t: number }

const KEEP_MS = 2000;
const CAP = 512;

/** Append while retaining at least two samples, at most 512, and the latest two seconds. */
export function pushSample<S extends Timed>(list: S[], s: S): void {
  list.push(s);
  const keepFrom = s.t - KEEP_MS;
  let drop = 0;
  while (drop < list.length - 2 && list[drop + 1].t < keepFrom) drop++;
  if (list.length - drop > CAP) drop = list.length - CAP;
  if (drop > 0) list.splice(0, drop);
}

/** Interpolate between samples; clamp times outside the sample range. */
export function valueAt<S extends Timed>(list: readonly S[], t: number, read: (s: S) => number): number {
  let i = list.length - 1;
  while (i > 0 && list[i].t > t) i--;
  const s = list[i];
  if (s.t > t) return read(s);
  const next = list[i + 1];
  if (!next || next.t <= s.t) return read(s);
  return read(s) + ((read(next) - read(s)) * (t - s.t)) / (next.t - s.t);
}

/** Window displacement per second; zero duration gives zero velocity. now is the value at t. */
export function windowRate<S extends Timed>(list: readonly S[], read: (s: S) => number, from: number, t: number, now: number): { rate: number; span: number; delta: number } {
  const span = t - from;
  if (span <= 0 || list.length === 0) return { rate: 0, span: Math.max(0, span), delta: 0 };
  const delta = now - valueAt(list, from, read);
  return { rate: (delta / span) * 1000, span, delta };
}
