import assert from "node:assert/strict";
import { reconcileResourceSelection } from "../src/resourceBreakdown/resourceSelection.ts";
import {
  resourceSegmentAtPercent,
  resourceTrackPercentAtClientX
} from "../src/resourceBreakdown/resourceTrackInteraction.ts";
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
assert.equal(resourceSegmentAtPercent(ranges, -10)?.segment.id, "first");
assert.equal(resourceSegmentAtPercent(ranges, 20)?.segment.id, "first");
assert.equal(resourceSegmentAtPercent(ranges, 21)?.segment.id, "second");
assert.equal(resourceSegmentAtPercent(ranges, 95)?.segment.id, "second");
assert.equal(resourceSegmentAtPercent([], 50), null);
assert.equal(resourceTrackPercentAtClientX(150, 100, 200), 25);
assert.equal(resourceTrackPercentAtClientX(20, 100, 200), 0);
assert.equal(resourceTrackPercentAtClientX(500, 100, 200), 100);

function bar(metricId: string, softwareIds: string[]): ResourceBreakdownBar {
  return {
    metricId,
    label: metricId,
    unit: "%",
    scaleMode: "capacity",
    totalValue: 0,
    capacityValue: 100,
    totalSystemPercent: 0,
    totalDisplay: "0%",
    software: softwareIds.map((softwareId, index) => ({
      softwareId,
      name: softwareId,
      kind: "Other",
      displayKind: "其他",
      value: index + 1,
      systemPercent: index + 1,
      displayValue: `${index + 1}%`,
      processCount: 0,
      processes: []
    }))
  };
}
