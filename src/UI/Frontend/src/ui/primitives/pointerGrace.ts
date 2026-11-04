export interface Point {
  x: number;
  y: number;
}

export interface Box {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

/** Convex hull of the departure point and destination corners; pointer transit ends outside it without a timer. */
export function graceArea(exit: Point, target: Box): Point[] {
  return convexHull([exit, ...corners(target)]);
}

/** The hull of both element boxes rejects departures away from the destination before creating a grace area. */
export function corridor(from: Box, to: Box): Point[] {
  return convexHull([...corners(from), ...corners(to)]);
}

function corners(box: Box): Point[] {
  return [
    { x: box.left, y: box.top },
    { x: box.right, y: box.top },
    { x: box.right, y: box.bottom },
    { x: box.left, y: box.bottom }
  ];
}

/** Andrew monotone-chain hull with consistent turns for containment checks. */
function convexHull(input: readonly Point[]): Point[] {
  const points = [...input].sort((a, b) => a.x - b.x || a.y - b.y);
  const lower: Point[] = [];
  for (const point of points) {
    while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], point) <= 0) lower.pop();
    lower.push(point);
  }
  const upper: Point[] = [];
  for (let index = points.length - 1; index >= 0; index -= 1) {
    const point = points[index];
    while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], point) <= 0) upper.pop();
    upper.push(point);
  }
  lower.pop();
  upper.pop();
  return [...lower, ...upper];
}

/** Convex hull containment, including edges. */
export function insideArea(area: readonly Point[], point: Point): boolean {
  if (area.length < 3) return false;
  for (let index = 0; index < area.length; index += 1) {
    if (cross(area[index], area[(index + 1) % area.length], point) < 0) return false;
  }
  return true;
}

function cross(origin: Point, a: Point, b: Point): number {
  return (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x);
}
