import assert from "node:assert/strict";
import {
  optimizationReportsDecoder
} from "../src/data/optimization/optimizationReportsDecoder.ts";
import {
  ResponseDecodeError
} from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = optimizationOverview();
const decoded = optimizationReportsDecoder.decode(source);
assert.equal(decoded.status.configuredRuleCount, 7);
assert.equal(decoded.status.availableRuleCount, 6);
assert.equal(decoded.status.lastEvaluationAt, source.status.lastEvaluationAt);
assert.deepEqual(decoded.reports, []);
assert.notEqual(decoded, source);

const invalidCases: Array<[unknown, string]> = [
  [null, "$"],
  [{ ...source, capturedAt: "not-a-date" }, "$.capturedAt"],
  [{ ...source, status: { ...source.status, configuredRuleCount: undefined } },
    "$.status.configuredRuleCount"],
  [{ ...source, status: { ...source.status, availableRuleCount: 8 } },
    "$.status.availableRuleCount"],
  [{ ...source, status: { ...source.status, activeReportCount: 1 } },
    "$.status.activeReportCount"],
  [{ ...source, status: { ...source.status, sampleIntervalSeconds: 0 } },
    "$.status.sampleIntervalSeconds"],
  [{
    ...source,
    status: {
      lastObservedAt: null,
      observedTargetCount: 6,
      activeReportCount: 0,
      trustedCount: 0,
      protectedCount: 0,
      recorderRunning: true,
      sampleIntervalSeconds: 10
    }
  }, "$.status.configuredRuleCount"]
];

for (const [value, expectedPath] of invalidCases) {
  assert.throws(
    () => optimizationReportsDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}

function optimizationOverview() {
  return {
    capturedAt: "2026-08-26T08:00:00.000Z",
    reports: [],
    trustedTargets: [],
    protectedTargets: [],
    status: {
      lastEvaluationAt: "2026-08-26T08:00:00.000Z",
      lastObservedAt: "2026-08-26T07:59:58.000Z",
      configuredRuleCount: 7,
      availableRuleCount: 6,
      activeReportCount: 0,
      trustedCount: 0,
      protectedCount: 0,
      sampleIntervalSeconds: 10
    }
  };
}
