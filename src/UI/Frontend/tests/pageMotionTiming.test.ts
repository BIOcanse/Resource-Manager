import assert from "node:assert/strict";
import { installProbe, type ProbePage } from "./browser/pageMotionJourney.ts";

Object.assign(globalThis, {
  window: {},
  document: new Proxy({}, { get() { throw new Error("geometry probe accessed the document"); } }),
  getComputedStyle() { throw new Error("geometry probe read style"); },
  PerformanceObserver: class {
    static supportedEntryTypes: string[] = [];
    observe() {}
    disconnect() {}
    takeRecords() { return []; }
  }
});
const page: ProbePage = { evaluate: async (callback, value) => callback(value) };
await installProbe(page, false);
const timing = window.__pm.snapshot();
assert.deepEqual(Object.keys(timing), ["t"]);
assert.ok(Number.isFinite(timing.t));
await installProbe(page, true);
assert.throws(() => window.__pm.snapshot(), /accessed the document/);
console.log("PASS page motion timing probe: time-only sampling never reads DOM or style");
