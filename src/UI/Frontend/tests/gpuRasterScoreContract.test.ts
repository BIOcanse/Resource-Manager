import assert from "node:assert/strict";
import { gpuPerformanceScoreSnapshotDecoder } from "../src/data/gpu/gpuSchedulingModelSource.ts";

const gpu = {
  gpuId: "opaque-gpu", index: 0, name: "Fixture GPU", rasterPerformanceScore: 7,
  defaultPerformanceScore: 10, performanceScore: 12, hasPerformanceOverride: true,
  isIntegrated: false, source: "fixture", matchedPreset: null, isPresetMatch: true,
  hasDedicatedMemoryMetrics: true
};
const decode = (item: unknown) => gpuPerformanceScoreSnapshotDecoder.decode({
  capturedAt: "2026-10-25T00:00:00Z", storagePath: "scores.json", gpus: [item]
});
assert.equal(decode(gpu).gpus[0].rasterPerformanceScore, 7);
assert.equal(decode({ ...gpu, rasterPerformanceScore: 0 }).gpus[0].rasterPerformanceScore, 0);
const { rasterPerformanceScore: _raster, ...missing } = gpu;
for (const item of [missing, ...[undefined, null, -1, Number.NaN].map(rasterPerformanceScore => ({ ...gpu, rasterPerformanceScore }))]) {
  assert.throws(() => decode(item), /rasterPerformanceScore/, "raster score is a required backend fact");
}
console.log("GPU raster performance score is required and never inferred from a default.");
