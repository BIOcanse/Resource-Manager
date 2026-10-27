import assert from "node:assert/strict";
import { graceArea, insideArea } from "../src/ui/primitives/pointerGrace.ts";

// 问号在 (141..161, 106..126)，说明在它上方 6 px：(8..328, 30..100)。
const button = { left: 141, right: 161, top: 106, bottom: 126 };
const popover = { left: 8, right: 328, top: 30, bottom: 100 };

// 从问号左侧边离开、斜着往说明去：一路都在区域里，直到进入说明。
const exit = { x: 141, y: 112 };
const area = graceArea(exit, popover);
for (let step = 0; step <= 40; step += 1) {
  const point = { x: exit.x - step, y: exit.y - step * 0.5 };
  assert.ok(insideArea(area, point), `a diagonal move toward the explanation left the grace area at ${JSON.stringify(point)}`);
}
// 从同一处水平往左走（不是往说明去）：马上走出区域。
assert.equal(insideArea(area, { x: exit.x - 3, y: exit.y }), false, "a sideways move away from the explanation stays in the grace area");
// 往下、往右离开：不在区域里。
assert.equal(insideArea(area, { x: exit.x, y: exit.y + 2 }), false);
assert.equal(insideArea(area, { x: 400, y: 60 }), false);
// 说明本身和两者之间的空隙都在区域里。
assert.ok(insideArea(area, { x: 150, y: 103 }), "the gap between the question mark and the explanation");
assert.ok(insideArea(area, { x: 300, y: 50 }), "the explanation itself");

// 从说明底边离开回到问号：区域朝问号收窄，偏到旁边就出去了。
const back = graceArea({ x: 40, y: 100 }, button);
assert.ok(insideArea(back, { x: 90, y: 103 }), "moving from the explanation toward the question mark");
assert.equal(insideArea(back, { x: 40, y: 110 }), false, "moving straight down away from the question mark");

console.log("pointerGrace: ok");
