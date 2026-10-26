import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

// The wire decoders own required score facts. The UI must not duplicate backend defaults.
const model = readFileSync(new URL("../src/features/gpuScheduling/GpuSchedulingModel.tsx", import.meta.url), "utf8");
assert.doesNotMatch(model, /defaultGpuSoftwareBaseScore|defaultBaseScoreForKind/);
assert.doesNotMatch(model, /rasterPerformanceScore:\s*scoreItem\?\.rasterPerformanceScore\s*\?\?\s*defaultPerformanceScore/);
assert.equal(existsSync(new URL("../src/features/gpuScheduling/gpuSoftwareBaseScore.ts", import.meta.url)), false,
  "the unused frontend copy of backend base-score defaults is removed");
console.log("GPU score presentation has no copied backend default table.");
