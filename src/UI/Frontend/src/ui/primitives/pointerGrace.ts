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

/**
 * 指针从一个元素移向另一个元素时可以经过的区域：离开处和另一个元素外框四角围成的凸包。
 * 指针还在这里面，就是在“移过去”的路上；走出这里就是离开了。纯几何，不看时间。
 */
export function graceArea(exit: Point, target: Box): Point[] {
  return convexHull([exit, ...corners(target)]);
}

/**
 * 两个元素之间的走廊：两个外框八个角围成的凸包，包含两个元素本身和它们之间的空隙。
 * 离开一个元素时，指针所在的那一点（离开事件的坐标）已经不在走廊里，就是没往另一个那边去：
 * 一次跳到远处（两次移动事件之间走了很远）时，离开事件的坐标就是那个远点，它和另一个围成的区域会把它自己包进去，
 * 只看 graceArea 的话指针停在那里不动就一直算在路上。
 */
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

/** 凸包顶点，相邻三点都是同一转向（Andrew 单调链，循环实现），insideArea 按同一转向判断。 */
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

/** 点在凸包里（含边上）。 */
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
