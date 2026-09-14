import assert from "node:assert/strict";
import {
  buildGpuSpecializedCounterIds,
  buildGpuSpecializedTelemetrySubscriptionUrl,
  getGpuSchedulingModelSource,
  gpuPerformanceScoreSnapshotDecoder,
  normalizeGpuSpecializedTelemetryQuery,
  normalizeGpuSchedulingModelQuery
} from "../src/data/gpu/gpuSchedulingModelSource.ts";

const capturedAt = "2026-08-29T12:00:00.000Z";

assert.deepEqual(
  normalizeGpuSchedulingModelQuery({ gpuIndexes: [2, 0, 2, -1, 1.5] }),
  { gpuIndexes: [0, 2] });
assert.deepEqual(
  normalizeGpuSpecializedTelemetryQuery({ counterIds: ["gpu.2.compute", "", "gpu.1.compute", "gpu.2.compute"] }),
  { counterIds: ["gpu.1.compute", "gpu.2.compute"] });
assert.equal(
  buildGpuSpecializedTelemetrySubscriptionUrl(
    { counterIds: ["gpu.2.compute", "gpu.1.compute"] },
    1000),
  "/api/metrics/gpu-specialized/subscribe?ids=gpu.1.compute&ids=gpu.2.compute&intervalMs=1000");
assert.deepEqual(buildGpuSpecializedCounterIds([1, 0, 1]), [
  "gpu.0.nvidia.rt.usage",
  "gpu.0.nvidia.cuda.usage",
  "gpu.0.amd.gcn.usage",
  "gpu.0.amd.rdna.usage",
  "gpu.0.amd.cdna.usage",
  "gpu.1.nvidia.rt.usage",
  "gpu.1.nvidia.cuda.usage",
  "gpu.1.amd.gcn.usage",
  "gpu.1.amd.rdna.usage",
  "gpu.1.amd.cdna.usage"
]);

const current = await getGpuSchedulingModelSource(
  fakeClient((url) => url.includes("performance-scores") ? "epoch-b" : "epoch-a"),
  { gpuIndexes: [] });
assert.equal(current.scoreSnapshot.gpus[0]?.gpuId, "gpu:0");
assert.equal(current.capturedAt, capturedAt);
assert.deepEqual(current.scoreOverrides.scoresByGpuId, { "gpu:0": 10 });

assert.throws(
  () => gpuPerformanceScoreSnapshotDecoder.decode({
    capturedAt,
    storagePath: "scores.json",
    gpus: [{
      ...scorePayload().gpus[0],
      performanceScore: Number.NaN
    }]
  }),
  /finite number/);

console.log("gpu scheduling model source tests passed");

function fakeClient(epochForUrl: (url: string) => string) {
  let sequence = 0;
  return {
    execute: async (descriptor: {
      key: string;
      url: string;
      decoder: { id: string; decode: (value: unknown) => unknown };
    }) => {
      const value = descriptor.decoder.decode(payloadFor(descriptor.url));
      sequence += 1;
      return {
        value,
        attempt: {
          id: `attempt-${sequence}`,
          sequence,
          key: descriptor.key,
          decoderId: descriptor.decoder.id,
          method: "GET",
          backendEpoch: epochForUrl(descriptor.url),
          startedAt: sequence,
          finishedAt: sequence,
          durationMs: 0
        }
      };
    }
  } as never;
}

function payloadFor(url: string): unknown {
  if (url.endsWith("/performance-overrides")) {
    return {
      scoresByGpuId: { "gpu:0": 10 },
      updatedAt: capturedAt,
      storagePath: "overrides.json"
    };
  }
  if (url.endsWith("/performance-scores")) {
    return scorePayload();
  }
  throw new Error(`Unexpected test URL: ${url}`);
}

function scorePayload() {
  return {
    capturedAt,
    storagePath: "scores.json",
    gpus: [{
      gpuId: "gpu:0",
      index: 0,
      name: "Test GPU",
      rasterPerformanceScore: 10,
      generationBonusScore: 0,
      useCaseBonusScore: 0,
      gpuPerformanceUseCases: ["general"],
      defaultPerformanceScore: 10,
      performanceScore: 10,
      hasPerformanceOverride: false,
      isIntegrated: false,
      source: "test",
      matchedPreset: null
    }]
  };
}
