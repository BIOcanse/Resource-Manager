import { resourceSegmentMinimumTargetPx, type ResourceSegmentLayout } from "./resourceSegmentLayout.ts";

/** Expand narrow hit targets by borrowing bounded width from adjacent wide segments while preserving their minimum width. Split deficits proportionally; return the nearest segment outside ranges or null without segments. */
export function resourceSegmentAtTrackX<T extends { value: number }>(
  ranges: readonly ResourceSegmentLayout<T>[],
  x: number,
  trackWidth: number)
: ResourceSegmentLayout<T> | null {
  if (ranges.length === 0) {
    return null;
  }

  const hits = resourceSegmentHitRanges(ranges, trackWidth);
  const position = Number.isFinite(x) ? x : 0;
  let nearest = 0;
  let nearestDistance = Number.POSITIVE_INFINITY;
  for (let index = 0; index < hits.length; index++) {
    const [left, right] = hits[index];
    if (position >= left && position < right) {
      return ranges[index];
    }
    const distance = position < left ? left - position : position - right;
    if (distance < nearestDistance) {
      nearest = index;
      nearestDistance = distance;
    }
  }
  return ranges[nearest];
}

/** Pointer ranges are half-open pixel intervals. */
export function resourceSegmentHitRanges<T extends { value: number }>(
  ranges: readonly ResourceSegmentLayout<T>[],
  trackWidth: number)
: [number, number][] {
  const width = Math.max(0, Number(trackWidth) || 0);
  const lefts = ranges.map((range) => clampNumber(range.left, 0, 100) * width / 100);
  const widths = ranges.map((range, index) =>
    Math.max(0, clampNumber(range.right, 0, 100) * width / 100 - lefts[index]));
  const minimum = ranges.length > 0 ? Math.min(resourceSegmentMinimumTargetPx, width / ranges.length) : 0;
  const hitWidths = [...widths];

  let index = 0;
  while (index < ranges.length) {
    if (widths[index] >= minimum) {
      index++;
      continue;
    }
    const start = index;
    while (index < ranges.length && widths[index] < minimum) {
      index++;
    }
    const end = index - 1;
    let need = 0;
    for (let tiny = start; tiny <= end; tiny++) {
      need += minimum - widths[tiny];
    }
    // A wide neighbor lends at most minimum on each side while retaining its own minimum.
    const spareLeft = start > 0 ? Math.min(minimum, Math.max(0, hitWidths[start - 1] - minimum)) : 0;
    const spareRight = end + 1 < ranges.length ? Math.min(minimum, Math.max(0, hitWidths[end + 1] - minimum)) : 0;
    let takeLeft = Math.min(spareLeft, need / 2);
    const takeRight = Math.min(spareRight, need - takeLeft);
    takeLeft = Math.min(spareLeft, need - takeRight);
    const share = need > 0 ? (takeLeft + takeRight) / need : 0;
    for (let tiny = start; tiny <= end; tiny++) {
      hitWidths[tiny] = widths[tiny] + (minimum - widths[tiny]) * share;
    }
    if (start > 0) hitWidths[start - 1] -= takeLeft;
    if (end + 1 < ranges.length) hitWidths[end + 1] -= takeRight;
  }

  const hits: [number, number][] = [];
  let left = lefts[0] ?? 0;
  for (const hitWidth of hitWidths) {
    hits.push([left, left + hitWidth]);
    left += hitWidth;
  }
  return hits;
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, Number(value) || 0));
}
