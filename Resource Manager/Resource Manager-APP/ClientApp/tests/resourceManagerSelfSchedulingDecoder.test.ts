import assert from "node:assert/strict";
import {
  resourceManagerSelfSchedulingDecoder
} from "../src/data/selfScheduling/resourceManagerSelfSchedulingDecoder.ts";
import {
  ResponseDecodeError
} from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = {
  cpuGrade: "optimize",
  gpuGrade: "normal",
  cpuUpdatedAt: "2026-08-22T10:20:30.123-05:00",
  gpuUpdatedAt: "2026-08-22T10:20:31.123-05:00",
  cpuPolicyId: "foreground-policy",
  gpuPolicyId: "default-policy",
  cpuReason: "foreground workload",
  gpuReason: "default state",
  sources: [{
    targetId: "software:example",
    displayName: "Example",
    policyId: "foreground-policy",
    cpuGrade: "optimize",
    gpuGrade: "normal",
    updatedAt: "2026-08-22T10:20:30.123-05:00",
    reason: "foreground workload",
    ignored: true
  }],
  futureField: "ignored"
};

const decoded = resourceManagerSelfSchedulingDecoder.decode(source);
assert.deepEqual(decoded, {
  cpuGrade: source.cpuGrade,
  gpuGrade: source.gpuGrade,
  cpuUpdatedAt: source.cpuUpdatedAt,
  gpuUpdatedAt: source.gpuUpdatedAt,
  cpuPolicyId: source.cpuPolicyId,
  gpuPolicyId: source.gpuPolicyId,
  cpuReason: source.cpuReason,
  gpuReason: source.gpuReason,
  sources: [{
    targetId: source.sources[0].targetId,
    displayName: source.sources[0].displayName,
    policyId: source.sources[0].policyId,
    cpuGrade: source.sources[0].cpuGrade,
    gpuGrade: source.sources[0].gpuGrade,
    updatedAt: source.sources[0].updatedAt,
    reason: source.sources[0].reason
  }]
});
assert.notEqual(decoded, source);
assert.notEqual(decoded.sources, source.sources);

const invalidCases: Array<[unknown, string]> = [
  [null, "$"],
  [{ ...source, cpuGrade: "frozen" }, "$.cpuGrade"],
  [{ ...source, gpuGrade: 1 }, "$.gpuGrade"],
  [{ ...source, cpuUpdatedAt: undefined }, "$.cpuUpdatedAt"],
  [{ ...source, gpuUpdatedAt: "not-a-date" }, "$.gpuUpdatedAt"],
  [{ ...source, cpuPolicyId: " " }, "$.cpuPolicyId"],
  [{ ...source, sources: {} }, "$.sources"],
  [{ ...source, sources: [null] }, "$.sources[0]"],
  [{ ...source, sources: [{ ...source.sources[0], targetId: "" }] }, "$.sources[0].targetId"],
  [{ ...source, sources: [{ ...source.sources[0], gpuGrade: "boost" }] }, "$.sources[0].gpuGrade"],
  [{ ...source, sources: [{ ...source.sources[0], updatedAt: 1 }] }, "$.sources[0].updatedAt"]
];

for (const [value, expectedPath] of invalidCases) {
  assert.throws(
    () => resourceManagerSelfSchedulingDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}
