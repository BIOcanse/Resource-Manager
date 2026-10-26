import assert from "node:assert/strict";
import { resolveResourceTooltipPlacement } from "../src/features/resourceBreakdown/resourceTooltipPlacement.ts";

// 提示放在悬停那一段的上方或下方（不盖住它），水平以它为中心，整个留在视口里（四边各留 8 px）。
const viewport = { left: 0, right: 1100, top: 0, bottom: 800 };
const width = 200;
const height = 90;

// 上方放得下：放上方，底边离这一段 8 px。
const middle = resolveResourceTooltipPlacement({ left: 500, right: 540, top: 300, bottom: 324 }, viewport, width, height);
assert.deepEqual(middle, { left: 420, top: 202, vertical: "above" });

// 靠左、靠右的段：水平收进视口。
assert.equal(resolveResourceTooltipPlacement({ left: 4, right: 20, top: 300, bottom: 324 }, viewport, width, height).left, 8);
assert.equal(resolveResourceTooltipPlacement({ left: 1080, right: 1096, top: 300, bottom: 324 }, viewport, width, height).left, 892);

// 靠近视口顶部：上方放不下就放下方，顶边离这一段 8 px。
const nearTop = resolveResourceTooltipPlacement({ left: 500, right: 540, top: 40, bottom: 64 }, viewport, width, height);
assert.deepEqual(nearTop, { left: 420, top: 72, vertical: "below" });

// 两边都放不下（视口很矮）：放在空间大的一边，再收进视口。
const cramped = resolveResourceTooltipPlacement({ left: 500, right: 540, top: 60, bottom: 84 }, { left: 0, right: 1100, top: 0, bottom: 150 }, width, height);
assert.equal(cramped.vertical, "below");
assert.equal(cramped.top, 150 - 8 - height);

// 每种情况下提示都整个在视口里，而且放得下时不压到这一段。
for (const anchor of [
  { left: 0, right: 6, top: 10, bottom: 34 },
  { left: 1094, right: 1100, top: 766, bottom: 790 },
  { left: 300, right: 900, top: 400, bottom: 424 }
]) {
  const placement = resolveResourceTooltipPlacement(anchor, viewport, width, height);
  assert.ok(placement.left >= 8 && placement.left + width <= 1092, JSON.stringify(placement));
  assert.ok(placement.top >= 8 && placement.top + height <= 792, JSON.stringify(placement));
  assert.ok(placement.top + height <= anchor.top || placement.top >= anchor.bottom, `covers the segment: ${JSON.stringify(placement)}`);
}

console.log("resourceTooltipPlacement: ok");
