// 资源条画布上与绘制无关的几步，画布组件（ResourceSegmentPaintPlane.tsx）只管把结果画出来：
// 一段过渡里每一帧各段在哪、离开的段收到哪里、新来的段从哪里长出来；
// （新来的段从宽度 0 长出来、离开的段收拢是平面移动让节点出现、消失的唯一裁定例外，用户 2026-10-26「保留生长」；
//  命中层的过渡登记在 presentation/animationStyle.ts 的 CSS_MOTIONS）
// 标签截到多长才放得下。

export interface ResourcePaintSegment {
  key: string;
  left: number;
  right: number;
  color: string;
  /** 共享部分占本段的比例，画在本段右端，用 --resource-shared-tint 提亮。 */
  sharedFraction?: number;
  distinctColor?: string;
  /** 斜纹：系统/驱动保留这类不是软件的段。 */
  hatched?: boolean;
  /** 放得下才画（按像素宽度判断）。 */
  label?: string;
  labelColor?: string;
}

/**
 * 这次更新里不在了的段和它收拢到的位置：它左边最近一个仍在的段的新右端（没有就是 0）。
 * 两者从同一处出发、到同一处停下，所以离开的段的左端始终贴着那一段的右端，过渡中条上不露底。
 */
export function leavingSegments(fromSegments: ResourcePaintSegment[], toSegments: ResourcePaintSegment[]) {
  const toByKey = new Map(toSegments.map((segment) => [segment.key, segment]));
  const leaving: { source: ResourcePaintSegment; anchor: number }[] = [];
  let anchor = 0;
  for (const segment of fromSegments) {
    const target = toByKey.get(segment.key);
    if (target) {
      anchor = target.right;
    } else {
      leaving.push({ source: segment, anchor });
    }
  }
  return leaving;
}

/**
 * 这次更新里新来的段从哪里长出来：宽度 0，位置是它左边最近一个仍在的段的旧右端（没有就是 0）。
 * 它的左端和那一段的右端、它的右端和右边让开的段的左端都从这里出发、到同一处停下，所以过渡中条上不露底。
 */
export function enteringSources(fromSegments: ResourcePaintSegment[], toSegments: ResourcePaintSegment[]) {
  const fromByKey = new Map(fromSegments.map((segment) => [segment.key, segment]));
  const entering = new Map<string, ResourcePaintSegment>();
  let anchor = 0;
  for (const target of toSegments) {
    const source = fromByKey.get(target.key);
    if (source) {
      anchor = source.right;
    } else {
      entering.set(target.key, { ...target, left: anchor, right: anchor });
    }
  }
  return entering;
}

/**
 * 过渡中的一帧（eased 是 0–1 的缓动进度）：离开的段在前、垫在下面，收向各自的位置；
 * 其余的段从 fromByKey 里的位置走到这次的位置（新来的段的起点由 enteringSources 给出）。
 */
export function transitionFrame(
  fromByKey: ReadonlyMap<string, ResourcePaintSegment>,
  leaving: readonly { source: ResourcePaintSegment; anchor: number }[],
  toSegments: readonly ResourcePaintSegment[],
  eased: number)
: ResourcePaintSegment[] {
  return [
    ...leaving.map(({ source, anchor }) => ({
      ...source,
      left: lerpNumber(source.left, anchor, eased),
      right: lerpNumber(source.right, anchor, eased)
    })),
    ...toSegments.map((target) => {
      const source = fromByKey.get(target.key) ?? target;
      return {
        ...target,
        left: lerpNumber(source.left, target.left, eased),
        right: lerpNumber(source.right, target.right, eased)
      };
    })
  ];
}

/** 放进 availableWidth 的标签：放不下时截短加省略号；截到不足 minimumCharacters 个字时不画（返回空串）。 */
export function fitCanvasLabel(
  textWidth: (text: string) => number,
  label: string,
  availableWidth: number,
  minimumCharacters = 1)
{
  if (textWidth(label) <= availableWidth) {
    return label;
  }

  const suffix = "…";
  if (textWidth(suffix) > availableWidth) {
    return "";
  }

  let low = 0;
  let high = label.length;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    const candidate = `${label.slice(0, middle)}${suffix}`;
    if (textWidth(candidate) <= availableWidth) {
      low = middle;
    } else {
      high = middle - 1;
    }
  }

  // 截在空格后面时去掉末尾空格，省略号紧跟着字（“Microsoft…”而不是“Microsoft …”）。
  const kept = label.slice(0, low).trimEnd();
  return kept.length >= minimumCharacters ? `${kept}${suffix}` : "";
}

function lerpNumber(from: number, to: number, progress: number) {
  return from + (to - from) * progress;
}
