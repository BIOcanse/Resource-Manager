import assert from "node:assert/strict";
import { resourceSegmentLayout, resourceSegmentLayerStyle } from "../src/features/resourceBreakdown/resourceSegmentLayout.ts";

const widths = (values: number[], fill = false) => resourceSegmentLayout(values.map(value => ({ value })), 100, { fill }).map(x => x.width);
assert.deepEqual(widths([99]), [99]);
assert.deepEqual(widths([60, 20]), [60, 20]);
assert.deepEqual(widths([60, 39, 1]), [60, 39, 1]);
assert.deepEqual(widths([60, 20, 1]), [60, 20, 1]);
assert.deepEqual(widths([100]), [100]);
assert.deepEqual(widths([0]), []);
assert.equal(widths([10, 20], true).reduce((a, b) => a + b), 100);
for (const dpr of [1, 1.25, 2]) {
  const layout = resourceSegmentLayout([{ value: 99.9 }, { value: 0.1 }], 100);
  const style = resourceSegmentLayerStyle(layout[0], {}, { contentWidth: 400, devicePixelRatio: dpr, segmentHeight: 24, segmentTop: 0 });
  assert.ok(parseFloat(style["--segment-render-width"]!) < 400);
  const empty = resourceSegmentLayerStyle(layout[1], {}, { contentWidth: 400, devicePixelRatio: dpr, segmentHeight: 24, segmentTop: 0 });
  assert.ok(parseFloat(empty["--segment-render-width"]!) > 0);
}
console.log("resourceSegmentLayout: ok");
