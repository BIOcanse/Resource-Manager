import assert from "node:assert/strict";
import { reconcileResourceSelection } from "../src/features/resourceBreakdown/resourceSelection.ts";
import {
  resourceSegmentAtTrackX,
  resourceSegmentHitRanges
} from "../src/features/resourceBreakdown/resourceTrackInteraction.ts";
import { resourceSegmentMinimumTargetPx } from "../src/features/resourceBreakdown/resourceSegmentLayout.ts";
import type { ResourceBreakdownBar } from "../src/types.ts";

const bars = [
  bar("cpu.usage", ["software-a", "software-b"]),
  bar("memory.percent", ["software-c"])
];

assert.equal(reconcileResourceSelection(bars, null), null);
assert.deepEqual(reconcileResourceSelection([
  bar("cpu.usage", ["software-b", "software-a"])
], {
  metricId: "cpu.usage",
  softwareId: "software-a",
  index: 0
}), {
  metricId: "cpu.usage",
  softwareId: "software-a",
  index: 1
});
assert.equal(reconcileResourceSelection([
  bar("cpu.usage", ["software-b"])
], {
  metricId: "cpu.usage",
  softwareId: "software-a",
  index: 1
}), null);
assert.equal(reconcileResourceSelection([
  bar("memory.percent", ["software-c"])
], {
  metricId: "cpu.usage",
  softwareId: "software-a",
  index: 0
}), null);
assert.equal(reconcileResourceSelection([], {
  metricId: "cpu.usage",
  softwareId: "software-a",
  index: 0
}), null);

const ranges = [
  { segment: { value: 20, id: "first" }, index: 0, left: 0, right: 20, width: 20 },
  { segment: { value: 30, id: "second" }, index: 1, left: 20, right: 50, width: 30 }
];
// 200 px 宽的轨道：0–40 px 是第一段，40–100 px 是第二段，右边没有段时取最近的一段。
assert.equal(resourceSegmentAtTrackX(ranges, -10, 200)?.segment.id, "first");
assert.equal(resourceSegmentAtTrackX(ranges, 39, 200)?.segment.id, "first");
assert.equal(resourceSegmentAtTrackX(ranges, 41, 200)?.segment.id, "second");
assert.equal(resourceSegmentAtTrackX(ranges, 190, 200)?.segment.id, "second");
assert.equal(resourceSegmentAtTrackX([], 50, 200), null);

// 很窄的段（这里 1000 px 里 0.3 px、0.2 px）指针也能指到：一串窄段一起向两边紧挨着的宽段借宽度，
// 每段至少 resourceSegmentMinimumTargetPx；离得远的分界和画出来的一样。
const layout = (values: [string, number][]) => {
  let left = 0;
  return values.map(([id, width], index) => {
    const item = { segment: { value: width, id }, index, left, right: left + width, width };
    left += width;
    return item;
  });
};
const tiny = layout([["big", 40], ["middle", 30], ["tinyA", 0.03], ["tinyB", 0.02], ["after", 29.95]]);
const hits = resourceSegmentHitRanges(tiny, 1000);
assert.deepEqual(hits[0], [0, 400], "boundaries away from the narrow run stay where they are drawn");
for (const index of [2, 3]) {
  assert.ok(hits[index][1] - hits[index][0] >= resourceSegmentMinimumTargetPx - 1e-9,
    `narrow segment ${index} must get a ${resourceSegmentMinimumTargetPx} px target: ${JSON.stringify(hits[index])}`);
}
assert.ok(Math.abs(hits[4][1] - 1000) < 1e-9, "the last range still ends at the track end");
for (let index = 1; index < hits.length; index++) {
  assert.ok(Math.abs(hits[index][0] - hits[index - 1][1]) < 1e-9, "hit ranges stay contiguous");
}
const centre = (index: number) => (hits[index][0] + hits[index][1]) / 2;
assert.equal(resourceSegmentAtTrackX(tiny, centre(2), 1000)?.segment.id, "tinyA");
assert.equal(resourceSegmentAtTrackX(tiny, centre(3), 1000)?.segment.id, "tinyB");
assert.equal(resourceSegmentAtTrackX(tiny, 690, 1000)?.segment.id, "middle");
// 段多到放不下时按段数均分，不借成负宽。
const crowded = layout(Array.from({ length: 50 }, (_, index) => [`s${index}`, 2] as [string, number]));
for (const [left, right] of resourceSegmentHitRanges(crowded, 100)) {
  assert.ok(right - left > 0, "every crowded segment keeps a positive range");
}

function bar(metricId: string, softwareIds: string[]): ResourceBreakdownBar {
  return {
    metricId,
    unit: "%",
    scaleMode: "capacity",
    totalValue: 0,
    capacityValue: 100,
    totalSystemPercent: 0,
    totalDisplay: "0%",
    software: softwareIds.map((softwareId, index) => ({
      softwareId,
      name: softwareId,
      kind: "other",
      displayKind: "其他",
      value: index + 1,
      systemPercent: index + 1,
      displayValue: `${index + 1}%`,
      processCount: 0,
      processes: []
    }))
  };
}
