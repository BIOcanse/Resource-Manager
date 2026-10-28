import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  optimizationReportsDecoder,
  reportEntryStates
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

for (const evaluationState of ["completed", "failed", "notEvaluated"] as const) {
  const value = { ...source, status: { ...source.status, evaluationState,
    evaluationFailureReason: evaluationState === "failed" ? "IOException: failed" : null } };
  const status = optimizationReportsDecoder.decode(value).status;
  assert.equal(status.evaluationState, evaluationState);
  assert.deepEqual(status.unavailableRules, ["cpu_throttle_temperature"]);
}

// 报告生命周期（docs/report_lifecycle_2026-10-28.md）：条目是 active 或 history，信任是单独的布尔；activeReportCount 只数 active。
const lifecycle = {
  ...source,
  reports: [report("report-active", "active", false), report("report-history", "history", true)],
  status: { ...source.status, activeReportCount: 1, unrecordedReportCount: 3 }
};
const decodedLifecycle = optimizationReportsDecoder.decode(lifecycle);
assert.deepEqual(decodedLifecycle.reports.map(item => [item.id, item.state, item.trusted]),
  [["report-active", "active", false], ["report-history", "history", true]]);
assert.equal(decodedLifecycle.status.unrecordedReportCount, 3);

const invalidCases: Array<[unknown, string]> = [
  [{ ...lifecycle, reports: [{ ...lifecycle.reports[0], state: "trusted_suppressed" }, lifecycle.reports[1]] }, "$.reports[0].state"],
  [{ ...lifecycle, reports: [{ ...lifecycle.reports[0], trusted: undefined }, lifecycle.reports[1]] }, "$.reports[0].trusted"],
  [{ ...lifecycle, status: { ...lifecycle.status, activeReportCount: 2 } }, "$.status.activeReportCount"],
  [{ ...lifecycle, status: { ...lifecycle.status, unrecordedReportCount: undefined } }, "$.status.unrecordedReportCount"],
  [{ ...lifecycle, status: { ...lifecycle.status, unrecordedReportCount: -1 } }, "$.status.unrecordedReportCount"],
  [null, "$"],
  [{ ...source, status: { ...source.status, unavailableRules: [] } }, "$.status.unavailableRules"],
  [{ ...source, status: { ...source.status, evaluationState: "ready" } }, "$.status.evaluationState"],
  [{ ...source, status: { ...source.status, unavailableRules: null } }, "$.status.unavailableRules"],
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
      unrecordedReportCount: 0,
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

function report(id: string, state: string, trusted: boolean) {
  const at = "2026-08-26T07:50:00.000Z";
  return {
    id, type: "suspected_stray_process", state, trusted, severity: "info", confidence: "high",
    createdAt: at, updatedAt: at, firstObservedAt: at, lastObservedAt: at,
    context: { contextKind: "normal", foregroundProcessId: null, foregroundProcessName: null, foregroundExecutablePath: null,
      foregroundSoftwareId: null, foregroundSoftwareName: null, confidence: "high", evidence: [] },
    target: { targetType: "process_instance", targetKey: `process:${id}`, name: "pwsh", softwareId: null, softwareName: null,
      softwareKind: null, displayKind: null, processNames: ["pwsh"], processIds: [424242], driveLetter: null },
    evidence: { process: null, resourceKind: "memory", averageValue: 1, peakValue: 1, currentValue: 1, valueUnit: "B",
      activeSampleCount: 1, sampleCount: 1, durationSeconds: 0 },
    suggestedActions: []
  };
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
      evaluationState: "completed",
      evaluationFailureReason: null,
      unavailableRules: ["cpu_throttle_temperature"],
      configuredRuleCount: 7,
      availableRuleCount: 6,
      activeReportCount: 0,
      unrecordedReportCount: 0,
      trustedCount: 0,
      protectedCount: 0,
      sampleIntervalSeconds: 10
    }
  };
}

// state 的取值与后端 OptimizationReportStates 的常量逐个相等。
const backendStates = readFileSync(new URL("../../../Core/Domain/Optimization/OptimizationReportItem.cs", import.meta.url), "utf8");
const statesBody = /public static class OptimizationReportStates\s*\{([^}]*)\}/.exec(backendStates)?.[1];
assert.ok(statesBody !== undefined, "backend OptimizationReportStates exists");
assert.deepEqual([...reportEntryStates], [...statesBody.matchAll(/const string \w+ = "([^"]+)"/g)].map(match => match[1]),
  "report entry states match the backend");
