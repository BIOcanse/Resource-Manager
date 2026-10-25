// 按时间排列的采样：有界保存、取某一时刻的值、窗口内的平均速度。指针速度和逻辑中心的速度用同一套算法。

export interface Timed { t: number }

const KEEP_MS = 2000;
const CAP = 512;

/** 追加一个采样，只留最近 2 s、最多 512 个（至少留两个，供插值）。 */
export function pushSample<S extends Timed>(list: S[], s: S): void {
  list.push(s);
  const keepFrom = s.t - KEEP_MS;
  let drop = 0;
  while (drop < list.length - 2 && list[drop + 1].t < keepFrom) drop++;
  if (list.length - drop > CAP) drop = list.length - CAP;
  if (drop > 0) list.splice(0, drop);
}

/** 某一时刻的值：落在两个采样之间时线性插值；早于第一个采样取第一个，晚于最后一个取最后一个。 */
export function valueAt<S extends Timed>(list: readonly S[], t: number, read: (s: S) => number): number {
  let i = list.length - 1;
  while (i > 0 && list[i].t > t) i--;
  const s = list[i];
  if (s.t > t) return read(s);
  const next = list[i + 1];
  if (!next || next.t <= s.t) return read(s);
  return read(s) + ((read(next) - read(s)) * (t - s.t)) / (next.t - s.t);
}

/** 窗口速度（每秒）：从 from 到 t 的位移 ÷ 时长；时长为 0 时速度为 0。now 是 t 时刻的值。 */
export function windowRate<S extends Timed>(list: readonly S[], read: (s: S) => number, from: number, t: number, now: number): { rate: number; span: number; delta: number } {
  const span = t - from;
  if (span <= 0 || list.length === 0) return { rate: 0, span: Math.max(0, span), delta: 0 };
  const delta = now - valueAt(list, from, read);
  return { rate: (delta / span) * 1000, span, delta };
}
