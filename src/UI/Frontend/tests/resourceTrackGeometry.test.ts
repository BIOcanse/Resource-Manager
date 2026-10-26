import assert from "node:assert/strict";
import { createEffect, createRoot } from "solid-js";
import { createResourceTrackGeometry } from "../src/features/resourceBreakdown/useResourceTrackGeometry.ts";

const events: string[] = [];
const observed = new Set<Element>();
let deliver!: (entries: unknown[]) => void;
Object.assign(globalThis, {
  window: { devicePixelRatio: 2 },
  ResizeObserver: class {
    constructor(callback: typeof deliver) { deliver = callback; }
    observe(element: Element) { observed.add(element); }
    unobserve(element: Element) { observed.delete(element); }
    disconnect() { observed.clear(); }
  },
  getComputedStyle: () => ({ borderTopWidth: "1px", borderBottomWidth: "1px" })
});
const track = (name: string) => ({
  getBoundingClientRect() { events.push(`read ${name}`); return { top: 10, height: 16 }; },
  clientWidth: 200
}) as HTMLElement;
let geometries!: ReturnType<typeof createResourceTrackGeometry>[];
const dispose = createRoot(dispose => {
  geometries = [createResourceTrackGeometry(), createResourceTrackGeometry()];
  geometries.forEach((geometry, index) => {
    geometry.observeTrack(track(String(index)));
    createEffect(() => { if (geometry.contentWidth()) events.push(`write ${index}`); });
  });
  return dispose;
});
assert.deepEqual(events, [], "binding a newly mounted bar does not read layout or style");
deliver([...observed].map(target => ({ target, borderBoxSize: [{ inlineSize: 200, blockSize: 16 }] })));
assert.deepEqual(events, ["read 0", "read 1", "write 0", "write 1"]);
for (const geometry of geometries) {
  assert.equal(geometry.contentWidth(), 200);
  assert.equal(geometry.devicePixelRatio(), 2);
  assert.equal(geometry.segmentTop(), 1);
  assert.equal(geometry.segmentHeight(), 12);
}
dispose();
assert.equal(observed.size, 0);
console.log("PASS resource track geometry: no mount reads, batched publication, pixel alignment, cleanup");