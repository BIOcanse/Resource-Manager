import assert from "node:assert/strict";
import { HELP_HINT_SPACING, RESOURCE_TOOLTIP_SPACING, resolveAnchoredPlacement } from "../src/ui/primitives/anchoredPlacement.ts";

// 资源条提示（ResourceTooltipOverlay）：放在悬停那一段的上方或下方（不盖住它），水平以它为中心，整个留在视口里（四边各留 8 px）。
const tooltip = RESOURCE_TOOLTIP_SPACING;
const viewport = { left: 0, right: 1100, top: 0, bottom: 800 };
const width = 200;
const height = 90;
const place = (anchor: { left: number; right: number; top: number; bottom: number }, area = viewport, spacing = tooltip) =>
  resolveAnchoredPlacement(anchor, area, width, height, spacing);

// 上方放得下：放上方，底边离这一段 8 px。
assert.deepEqual(place({ left: 500, right: 540, top: 300, bottom: 324 }), { left: 420, top: 202, vertical: "above" });

// 靠左、靠右的段：水平收进视口。
assert.equal(place({ left: 4, right: 20, top: 300, bottom: 324 }).left, 8);
assert.equal(place({ left: 1080, right: 1096, top: 300, bottom: 324 }).left, 892);

// 靠近视口顶部：上方放不下就放下方，顶边离这一段 8 px。
assert.deepEqual(place({ left: 500, right: 540, top: 40, bottom: 64 }), { left: 420, top: 72, vertical: "below" });

// 两边都放不下（视口很矮）：放在空间大的一边，再收进视口。
const cramped = place({ left: 500, right: 540, top: 60, bottom: 84 }, { left: 0, right: 1100, top: 0, bottom: 150 });
assert.equal(cramped.vertical, "below");
assert.equal(cramped.top, 150 - 8 - height);

// 每种情况下提示都整个在视口里，而且放得下时不压到这一段。
for (const anchor of [
  { left: 0, right: 6, top: 10, bottom: 34 },
  { left: 1094, right: 1100, top: 766, bottom: 790 },
  { left: 300, right: 900, top: 400, bottom: 424 }
]) {
  const placement = place(anchor);
  assert.ok(placement.left >= 8 && placement.left + width <= 1092, JSON.stringify(placement));
  assert.ok(placement.top >= 8 && placement.top + height <= 792, JSON.stringify(placement));
  assert.ok(placement.top + height <= anchor.top || placement.top >= anchor.bottom, `covers the segment: ${JSON.stringify(placement)}`);
}

// 问号说明（HelpHint）：离问号 6 px，不盖住问号，水平上跨过问号（斜着移过去的区域由 pointerGrace 判断），整个在视口里。
for (const anchor of [
  { left: 500, right: 520, top: 300, bottom: 320 },
  { left: 500, right: 520, top: 20, bottom: 40 },
  { left: 1070, right: 1090, top: 300, bottom: 320 },
  { left: 10, right: 30, top: 300, bottom: 320 }
]) {
  const placement = place(anchor, viewport, HELP_HINT_SPACING);
  const bottom = placement.top + height;
  const gap = placement.vertical === "above" ? anchor.top - bottom : placement.top - anchor.bottom;
  assert.equal(gap, HELP_HINT_SPACING.gap, `the explanation is not 6 px from the question mark: ${JSON.stringify(placement)}`);
  assert.ok(placement.left <= anchor.left && placement.left + width >= anchor.right, `the explanation does not span the question mark: ${JSON.stringify(placement)}`);
  assert.ok(placement.left >= HELP_HINT_SPACING.inset && placement.left + width <= 1100 - HELP_HINT_SPACING.inset, JSON.stringify(placement));
}

console.log("anchoredPlacement: ok");
