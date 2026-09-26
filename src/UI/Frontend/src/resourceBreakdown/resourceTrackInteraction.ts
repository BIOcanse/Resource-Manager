import type { ResourceSegmentLayout } from "./resourceSegmentLayout";

export function resourceSegmentAtPercent<T extends { value: number }>(
  ranges: ResourceSegmentLayout<T>[],
  percent: number)
: ResourceSegmentLayout<T> | null {
  if (ranges.length === 0) {
    return null;
  }

  const position = clampNumber(Number(percent) || 0, 0, 100);
  for (const range of ranges) {
    if (position >= range.left && position <= range.right) {
      return range;
    }
  }

  let nearest: ResourceSegmentLayout<T> | null = null;
  let nearestDistance = Number.POSITIVE_INFINITY;
  for (const range of ranges) {
    const distance = position < range.left
      ? range.left - position
      : position - range.right;
    if (distance < nearestDistance) {
      nearest = range;
      nearestDistance = distance;
    }
  }

  return nearest;
}

export function resourceTrackPercentAtClientX(
  clientX: number,
  trackLeft: number,
  trackWidth: number)
{
  return trackWidth > 0
    ? clampNumber((clientX - trackLeft) * 100 / trackWidth, 0, 100)
    : 0;
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
