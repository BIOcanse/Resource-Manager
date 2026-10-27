import { resourceSegmentMinimumTargetPx, type ResourceSegmentLayout } from "./resourceSegmentLayout.ts";

/**
 * 指针在轨道内容区横坐标 x（px，0 是内容区左边）时落在哪一段。
 *
 * 窄段（画出来不到 min(resourceSegmentMinimumTargetPx, 轨道宽 / 段数) 的段）的指针范围放宽：
 * 连着的一串窄段一起向它左右两边紧挨着的宽段借宽度，各借一半，一边不够另一边补；
 * 每个宽段向每一边最多借出这个最小宽度，自己至少留下这个宽度。借到的宽度按各窄段差多少分。
 * 所以分界只在窄段两边挪开至多几个像素，宽段画出来的地方除了边上这几个像素都指到它自己：
 * 一长串窄段借不够时各自不到最小宽度（键盘仍按顺序逐段走），而不是把旁边的宽段挤成几个像素——
 * 后端给物理内存条上百个很小的服务时，按段数均分会让画在条上三分之一的系统/驱动保留只剩 6 px 可指，
 * 指针在它上面落到的全是小服务。
 * 落在所有段之外时取最近的一段；没有段时为 null。
 */
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

/** 每段的指针范围 [左, 右)（px）；见 resourceSegmentAtTrackX。 */
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
    // 每个宽段向每一边至多借出 minimum：左边的宽段这一边还没借过，右边的宽段还是画出来的宽度。
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
