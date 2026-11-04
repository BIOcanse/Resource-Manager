// Compute segment transitions separately from canvas drawing.
// Resolve frame geometry, exit targets, and entry origins.
// Entering segments grow from zero width; departing segments collapse.
// Keep hit-layer timing aligned with presentation timing.
// Fit labels to available width.

export interface ResourcePaintSegment {
  key: string;
  left: number;
  right: number;
  color: string;
  /** Draw the shared fraction at the right edge with the shared tint. */
  sharedFraction?: number;
  distinctColor?: string;
  /** Hatch nonsoftware reserved segments. */
  hatched?: boolean;
  /** Draw labels only when their pixel width fits. */
  label?: string;
  labelColor?: string;
}

/** Collapse exits toward the nearest surviving left neighbor's new edge, or zero, to avoid gaps. */
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

/** Grow entries from the nearest surviving left neighbor's old edge, or zero, to avoid gaps. */
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

/** Paint exits underneath and interpolate surviving and entering segments using eased progress. */
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

/** Truncate with an ellipsis; return empty when fewer than minimumCharacters remain. */
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

  // Trim trailing spaces before appending the ellipsis.
  const kept = label.slice(0, low).trimEnd();
  return kept.length >= minimumCharacters ? `${kept}${suffix}` : "";
}

function lerpNumber(from: number, to: number, progress: number) {
  return from + (to - from) * progress;
}
