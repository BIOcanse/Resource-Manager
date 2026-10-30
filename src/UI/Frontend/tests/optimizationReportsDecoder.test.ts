import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  optimizationReportsDecoder,
  protectedTargetListDecoder,
  reportEntryStates
} from "../src/data/optimization/optimizationReportsDecoder.ts";
import { protectionSources } from "../src/data/optimization/softwareProtection.ts";
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

// 保护列表（GET /api/optimization/protection，docs/scheduling_protection_2026-10-29.md）：条目和报告总览里的
// protectedTargets 同一个解码；新加的五个字段可以省略或为 null，替换默认的用户条目同时带默认的等级和来源。
const catalogEntry = protectedTarget({ id: "software-catalog", protectionSource: "software_catalog", policyGroupId: "meeting-capture-password" });
const replacedEntry = protectedTarget({ id: "software-user", protectionSource: "software_detail", protectionLevel: 0,
  defaultProtectionLevel: 1, defaultProtectionSource: "software_catalog", policyGroupId: "meeting-capture-password" });
const processEntry = protectedTarget({ id: "process-dwm", targetType: "process", targetKey: "process-name:dwm", softwareId: null,
  softwareName: null, softwareKind: null, protectionSource: "software_catalog", processName: "dwm", executablePath: null });
const legacyEntry = Object.fromEntries(Object.entries(protectedTarget({ id: "legacy" }))
  .filter(([key]) => !["defaultProtectionLevel", "defaultProtectionSource", "policyGroupId", "processName", "executablePath"].includes(key)));
const decodedList = protectedTargetListDecoder.decode([catalogEntry, replacedEntry, processEntry, legacyEntry]);
assert.equal(decodedList.length, 4);
assert.equal(decodedList[0]!.policyGroupId, "meeting-capture-password");
assert.equal(decodedList[0]!.defaultProtectionSource, null);
assert.deepEqual([decodedList[1]!.protectionLevel, decodedList[1]!.defaultProtectionLevel, decodedList[1]!.defaultProtectionSource],
  [0, 1, "software_catalog"]);
assert.deepEqual([decodedList[2]!.processName, decodedList[2]!.executablePath], ["dwm", null]);
assert.deepEqual([decodedList[3]!.defaultProtectionLevel, decodedList[3]!.defaultProtectionSource, decodedList[3]!.policyGroupId,
  decodedList[3]!.processName, decodedList[3]!.executablePath], [null, null, null, null, null]);
assert.ok(Object.isFrozen(decodedList));
// 报告总览里的 protectedTargets 也带这些字段。
assert.equal(optimizationReportsDecoder.decode({ ...source, protectedTargets: [replacedEntry],
  status: { ...source.status, protectedCount: 1 } }).protectedTargets[0]!.defaultProtectionSource, "software_catalog");

const invalidLists: Array<[unknown, string]> = [
  [null, "$"],
  [{ targets: [] }, "$"],
  [[{ softwareId: "x", targetKey: "software:x", protectionLevel: 1 }], "$[0].id"],
  [[catalogEntry, catalogEntry], "$[1].id"],
  [[{ ...catalogEntry, protectionLevel: -1 }], "$[0].protectionLevel"],
  [[{ ...catalogEntry, defaultProtectionLevel: 1 }], "$[0].defaultProtectionSource"],
  [[{ ...catalogEntry, defaultProtectionSource: "software_catalog" }], "$[0].defaultProtectionLevel"],
  [[{ ...replacedEntry, defaultProtectionLevel: "1" }], "$[0].defaultProtectionLevel"],
  [[{ ...replacedEntry, defaultProtectionSource: "" }], "$[0].defaultProtectionSource"],
  [[{ ...processEntry, processName: 7 }], "$[0].processName"],
  [[{ ...processEntry, executablePath: false }], "$[0].executablePath"],
  [[{ ...catalogEntry, policyGroupId: [] }], "$[0].policyGroupId"]
];
for (const [value, expectedPath] of invalidLists) {
  assert.throws(
    () => protectedTargetListDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError && error.path === expectedPath,
    `protection list must fail at ${expectedPath}`);
}

function protectedTarget(overrides: Record<string, unknown>) {
  return {
    id: "entry", targetType: "software", targetKey: "software:frontend-harness.software",
    softwareId: "frontend-harness.software", softwareName: "示例软件", softwareKind: "other",
    protectedAt: "2026-10-29T08:00:00.000Z", protectionSource: "software_detail", createdFromReportId: "software-detail",
    lastVerifiedAt: null, state: "active", allowsPlacementAvoidance: true, protectionLevel: 1,
    defaultProtectionLevel: null, defaultProtectionSource: null, policyGroupId: null, processName: null, executablePath: null,
    ...overrides
  };
}

// 来源的取值与后端 OptimizationProtectionSources 逐个相等。后端常量随 codex/catalog-protection 进来；
// 这个检出里还没有时跳过并打印，合入后必须相等。
const backendProtection = readFileSync(new URL("../../../Core/Domain/Optimization/OptimizationProtection.cs", import.meta.url), "utf8");
const sourcesBody = /public static class OptimizationProtectionSources\s*\{([\s\S]*?)\r?\n\}/.exec(backendProtection)?.[1];
if (sourcesBody === undefined) {
  console.log("skip: backend OptimizationProtectionSources is not in this checkout yet");
} else {
  assert.deepEqual([...protectionSources].sort(),
    [...sourcesBody.matchAll(/const string \w+ = "([^"]+)"/g)].map(match => match[1]).sort(),
    "protection sources match the backend");
}
