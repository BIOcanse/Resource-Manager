import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  kernelLeakActionKinds, kernelLeakFailures, kernelLeakMetadataStatuses, kernelLeakReportDecoder, kernelLeakReportStatuses,
  kernelLeakItemStates, kernelLeakResourceKinds, kernelLeakRestartPolicies, kernelLeakRiskLevels, kernelLeakUnits, type KernelLeakReport
} from "../src/data/kernelLeaks/kernelLeakReport.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { kernelLeakRecordedFrames, kernelLeakReportFrames } from "./fixtures/kernelLeakReports.ts";

// 合同 v3：后端交接里记录的线上例子（final-message.md「Recorded wire examples」，生命周期字段按 docs/report_lifecycle_2026-10-28.md 补上）。
const recordedObservation = {
  startedAt: "1970-01-01T00:00:00+00:00", elapsedSeconds: 1, sampleCount: 2, requiredSeconds: 2, requiredSampleCount: 3,
  previousRunFailure: null, restartPolicy: "NextInterval"
} as const;
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakRecordedFrames.observing), {
  status: "Observing", capturedAt: "1970-01-01T00:00:01+00:00", observation: recordedObservation, unavailableHolderCount: 0,
  items: [], unrecordedItemCount: 0
} satisfies KernelLeakReport);

const reservedActions = [
  { action: "RestartHolder", enabled: false, disabledReason: { domain: 7, code: 1, args: [] }, risk: { domain: 7, code: 2, args: [] }, riskLevel: "Warning" },
  { action: "ForceReleaseHandles", enabled: false, disabledReason: { domain: 7, code: 1, args: [] }, risk: { domain: 7, code: 3, args: [] }, riskLevel: "High" }
] as const;
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakRecordedFrames.suspected), {
  status: "Suspected",
  capturedAt: "1970-01-01T00:00:02+00:00",
  observation: { ...recordedObservation, elapsedSeconds: 2, sampleCount: 3 },
  unavailableHolderCount: 0,
  unrecordedItemCount: 0,
  items: [{
    state: "active", firstObservedAt: "1970-01-01T00:00:02+00:00", lastObservedAt: "1970-01-01T00:00:02+00:00",
    resourceKind: "ProcessHandles", unit: "count", currentValue: 1200, growthPerSecond: 100,
    observationSeconds: 2, sampleCount: 3, growth: 200,
    holder: {
      processId: 7, startKey: "42",
      image: { status: "Current", name: "recorded-holder", path: "C:\\recorded\\holder.exe", productName: "recorded-product" },
      services: { status: "Current", names: ["recorded-service"] }
    },
    actions: reservedActions
  }]
} satisfies KernelLeakReport);

assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakRecordedFrames.failedRun), {
  status: "Unavailable", capturedAt: "1970-01-01T00:00:01+00:00", failure: "CapacityExceeded",
  observation: { ...recordedObservation, sampleCount: 1 }, unavailableHolderCount: 0, items: [], unrecordedItemCount: 0
} satisfies KernelLeakReport);

assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakRecordedFrames.nextRun), {
  status: "Observing", capturedAt: "1970-01-01T00:00:02+00:00",
  observation: {
    ...recordedObservation, startedAt: "1970-01-01T00:00:02+00:00", elapsedSeconds: 0, sampleCount: 1,
    previousRunFailure: { startedAt: "1970-01-01T00:00:00+00:00", failedAt: "1970-01-01T00:00:01+00:00", failure: "CapacityExceeded" }
  },
  unavailableHolderCount: 0, items: [], unrecordedItemCount: 0
} satisfies KernelLeakReport);

assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakRecordedFrames.notObserved), {
  status: "NotObserved", capturedAt: null,
  observation: { ...recordedObservation, startedAt: null, elapsedSeconds: 0, sampleCount: 0 },
  unavailableHolderCount: 0, items: [], unrecordedItemCount: 0
} satisfies KernelLeakReport);

// 数据源在第一帧之前就不可用：失败只在报告的 failure 上，没有开始时间，也没有伪造的上一轮失败。
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakRecordedFrames.sourceBeforeFirstFrame), {
  status: "Unavailable", capturedAt: "2026-10-27T19:00:01+00:00", failure: "SourceUnavailable",
  observation: {
    startedAt: null, elapsedSeconds: 0, sampleCount: 0, requiredSeconds: 600, requiredSampleCount: 8,
    previousRunFailure: null, restartPolicy: "NextInterval"
  },
  unavailableHolderCount: 0, items: [], unrecordedItemCount: 0
} satisfies KernelLeakReport);

// 每个模拟场景（除了故意不合合同的那一个）都能解码，状态按帧对应。
for (const [name, frame] of Object.entries(kernelLeakReportFrames)) {
  if (name === "invalid") {
    assert.throws(() => kernelLeakReportDecoder.decode(frame),
      (error: unknown) => error instanceof ResponseDecodeError && error.path === "$.items[0].resourceKind", name);
    continue;
  }
  const decoded = kernelLeakReportDecoder.decode(frame);
  assert.equal(decoded.status, frame.status, name);
  assert.equal(decoded.unavailableHolderCount, frame.unavailableHolderCount, name);
  assert.deepEqual(decoded.observation, frame.observation, name);
  assert.equal(decoded.items.length, frame.items.length, name);
  assert.equal(decoded.unrecordedItemCount, frame.unrecordedItemCount, name);
}

// 条目任何状态下都可以有：本轮没有活动条目时留着历史条目，观察没有运行或本轮失败时条目也照常带着。
for (const [name, status] of [["empty-with-history", "Empty"], ["notObserved-with-history", "NotObserved"], ["unavailable-with-history", "Unavailable"]] as const) {
  const decoded = kernelLeakReportDecoder.decode(kernelLeakReportFrames[name]);
  assert.equal(decoded.status, status, name);
  assert.ok(decoded.items.length > 0, name);
}
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakReportFrames["empty-with-history"]).items.map(item => item.state), ["history", "history"]);
assert.equal(kernelLeakReportDecoder.decode(kernelLeakReportFrames["suspected-unrecorded"]).unrecordedItemCount, 2);

// 持有者信息：各自的状态不是 Current 时不带值；Current 且服务名单为空表示没有对应服务；后端省略产品名就是 null。
const suspected = kernelLeakReportDecoder.decode(kernelLeakReportFrames.suspected);
if (suspected.status !== "Suspected") throw new Error("suspected scenario should decode as Suspected");
const holders = suspected.items.map(item => item.holder);
assert.deepEqual(holders.map(holder => holder.image.status), ["Current", "Current", "ProcessEnded", "Current", "Unavailable", "Current"]);
assert.deepEqual(suspected.items.map(item => item.state), ["active", "active", "history", "active", "active", "active"]);
assert.deepEqual(holders[2].image, { status: "ProcessEnded" });
assert.deepEqual(holders[2].services, { status: "ProcessEnded" });
assert.deepEqual(holders[4].image, { status: "Unavailable" });
assert.deepEqual(holders[4].services, { status: "Current", names: [] });
assert.deepEqual(holders[3].services, { status: "Current", names: ["Appinfo", "ProfSvc", "Schedule", "Winmgmt"] });
assert.equal(holders[5].image.status === "Current" ? holders[5].image.productName : "?", null);

// 不合合同：未知枚举、缺字段、类型不对、状态与内容不一致，都在边界上报出具体路径。
type Mutation = (frame: any) => void;
const invalidCases: Array<[string, Mutation, string]> = [
  ["not an object", frame => { frame.items = undefined; frame.status = undefined; }, "$.status"],
  ["unknown status", frame => { frame.status = "Warming"; }, "$.status"],
  ["removed failure NotObserved", frame => { frame.status = "Unavailable"; frame.items = []; frame.failure = "NotObserved"; }, "$.failure"],
  ["unknown failure", frame => { frame.failure = "Timeout"; }, "$.failure"],
  ["missing count", frame => { delete frame.unavailableHolderCount; }, "$.unavailableHolderCount"],
  ["negative count", frame => { frame.unavailableHolderCount = -1; }, "$.unavailableHolderCount"],
  ["time without offset", frame => { frame.capturedAt = "2026-10-27T10:42:30"; }, "$.capturedAt"],
  ["missing observation", frame => { delete frame.observation; }, "$.observation"],
  ["observation start without offset", frame => { frame.observation.startedAt = "1970-01-01T00:00:00"; }, "$.observation.startedAt"],
  ["negative elapsed", frame => { frame.observation.elapsedSeconds = -1; }, "$.observation.elapsedSeconds"],
  ["fractional sample count", frame => { frame.observation.sampleCount = 2.5; }, "$.observation.sampleCount"],
  ["missing required seconds", frame => { delete frame.observation.requiredSeconds; }, "$.observation.requiredSeconds"],
  ["missing required samples", frame => { delete frame.observation.requiredSampleCount; }, "$.observation.requiredSampleCount"],
  ["unknown restart policy", frame => { frame.observation.restartPolicy = "Manual"; }, "$.observation.restartPolicy"],
  ["previous failure None", frame => { frame.observation.previousRunFailure = { startedAt: "1970-01-01T00:00:00+00:00", failedAt: "1970-01-01T00:00:01+00:00", failure: "None" }; }, "$.observation.previousRunFailure.failure"],
  ["previous failure without time", frame => { frame.observation.previousRunFailure = { startedAt: "1970-01-01T00:00:00+00:00", failedAt: null, failure: "CapacityExceeded" }; }, "$.observation.previousRunFailure.failedAt"],
  ["unknown resource kind", frame => { frame.items[0].resourceKind = "FileCachePoolBytes"; }, "$.items[0].resourceKind"],
  ["unknown unit", frame => { frame.items[0].unit = "pages"; }, "$.items[0].unit"],
  ["removed unit bytes", frame => { frame.items[0].unit = "bytes"; }, "$.items[0].unit"],
  ["previous failure without start", frame => { frame.observation.previousRunFailure = { startedAt: null, failedAt: "1970-01-01T00:00:01+00:00", failure: "CapacityExceeded" }; }, "$.observation.previousRunFailure.startedAt"],
  ["missing unit", frame => { delete frame.items[0].unit; }, "$.items[0].unit"],
  ["fractional value", frame => { frame.items[0].currentValue = 12.5; }, "$.items[0].currentValue"],
  ["text value", frame => { frame.items[0].growth = "200"; }, "$.items[0].growth"],
  ["infinite rate", frame => { frame.items[0].growthPerSecond = null; }, "$.items[0].growthPerSecond"],
  ["negative observation", frame => { frame.items[0].observationSeconds = -1; }, "$.items[0].observationSeconds"],
  ["missing sample count", frame => { delete frame.items[0].sampleCount; }, "$.items[0].sampleCount"],
  ["non-decimal start key", frame => { frame.items[0].holder.startKey = "0x2a"; }, "$.items[0].holder.startKey"],
  ["numeric start key", frame => { frame.items[0].holder.startKey = 42; }, "$.items[0].holder.startKey"],
  ["flat v1 holder", frame => { frame.items[0].holder = { processId: 7, startKey: "42", name: "x", imagePath: "C:\\x.exe", imageStatus: "Current" }; }, "$.items[0].holder.image"],
  ["unknown image status", frame => { frame.items[0].holder.image.status = "Stale"; }, "$.items[0].holder.image.status"],
  ["current image without path", frame => { delete frame.items[0].holder.image.path; }, "$.items[0].holder.image.path"],
  ["current image without name", frame => { delete frame.items[0].holder.image.name; }, "$.items[0].holder.image.name"],
  ["blank product name", frame => { frame.items[0].holder.image.productName = ""; }, "$.items[0].holder.image.productName"],
  ["ended image with a name", frame => { frame.items[0].holder.image = { status: "ProcessEnded", name: "x" }; }, "$.items[0].holder.image.name"],
  ["unavailable image with a product", frame => { frame.items[0].holder.image = { status: "Unavailable", productName: "x" }; }, "$.items[0].holder.image.productName"],
  ["unknown service status", frame => { frame.items[0].holder.services.status = "Partial"; }, "$.items[0].holder.services.status"],
  ["missing service list", frame => { delete frame.items[0].holder.services.names; }, "$.items[0].holder.services.names"],
  ["empty service name", frame => { frame.items[0].holder.services.names = [""]; }, "$.items[0].holder.services.names[0]"],
  ["ended services with names", frame => { frame.items[0].holder.services = { status: "ProcessEnded", names: ["x"] }; }, "$.items[0].holder.services.names"],
  ["missing actions", frame => { delete frame.items[0].actions; }, "$.items[0].actions"],
  ["unknown action", frame => { frame.items[0].actions[0].action = "Suspend"; }, "$.items[0].actions[0].action"],
  ["duplicate action", frame => { frame.items[0].actions[1].action = "RestartHolder"; }, "$.items[0].actions[1].action"],
  ["enabled action", frame => { frame.items[0].actions[0].enabled = true; frame.items[0].actions[0].disabledReason = null; }, "$.items[0].actions[0].enabled"],
  ["disabled action without a reason", frame => { frame.items[0].actions[0].disabledReason = null; }, "$.items[0].actions[0].disabledReason"],
  ["risk without a code", frame => { delete frame.items[0].actions[1].risk.code; }, "$.items[0].actions[1].risk.code"],
  ["unknown risk level", frame => { frame.items[0].actions[1].riskLevel = "Critical"; }, "$.items[0].actions[1].riskLevel"],
  ["suspected without items", frame => { frame.items = []; }, "$.items"],
  ["suspected with only history items", frame => { frame.items[0].state = "history"; }, "$.items"],
  ["unknown item state", frame => { frame.items[0].state = "resolved"; }, "$.items[0].state"],
  ["capitalized item state", frame => { frame.items[0].state = "Active"; }, "$.items[0].state"],
  ["missing item state", frame => { delete frame.items[0].state; }, "$.items[0].state"],
  ["missing first observed time", frame => { delete frame.items[0].firstObservedAt; }, "$.items[0].firstObservedAt"],
  ["last observed without offset", frame => { frame.items[0].lastObservedAt = "1970-01-01T00:00:02"; }, "$.items[0].lastObservedAt"],
  ["last observed before first", frame => { frame.items[0].lastObservedAt = "1970-01-01T00:00:01+00:00"; }, "$.items[0].lastObservedAt"],
  ["duplicate item", frame => { frame.items.push(structuredClone(frame.items[0])); }, "$.items[1]"],
  ["items out of order", frame => {
    frame.items.push({ ...structuredClone(frame.items[0]), firstObservedAt: "1970-01-01T00:00:01+00:00", holder: { ...frame.items[0].holder, processId: 8 } });
  }, "$.items[1].firstObservedAt"],
  ["missing unrecorded count", frame => { delete frame.unrecordedItemCount; }, "$.unrecordedItemCount"],
  ["negative unrecorded count", frame => { frame.unrecordedItemCount = -1; }, "$.unrecordedItemCount"],
  ["suspected with a failure", frame => { frame.failure = "SourceUnavailable"; }, "$.failure"],
  ["suspected without a time", frame => { frame.capturedAt = null; }, "$.capturedAt"],
  ["empty with an active item", frame => { frame.status = "Empty"; }, "$.items"],
  ["empty without a time", frame => { frame.status = "Empty"; frame.items = []; frame.capturedAt = null; }, "$.capturedAt"],
  ["observing with a failure", frame => { frame.status = "Observing"; frame.items = []; frame.failure = "CapacityExceeded"; }, "$.failure"],
  ["not observed with a failure", frame => { frame.status = "NotObserved"; frame.items = []; frame.failure = "SourceUnavailable"; }, "$.failure"],
  ["the removed status Disabled", frame => { frame.status = "Disabled"; frame.items = []; }, "$.status"],
  ["unavailable without a reason", frame => { frame.status = "Unavailable"; frame.items = []; }, "$.failure"]
];
for (const [name, mutate, expectedPath] of invalidCases) {
  const frame = structuredClone(kernelLeakRecordedFrames.suspected) as any;
  mutate(frame);
  assert.throws(() => kernelLeakReportDecoder.decode(frame),
    (error: unknown) => error instanceof ResponseDecodeError && error.path === expectedPath,
    `${name} should fail at ${expectedPath}`);
}
assert.throws(() => kernelLeakReportDecoder.decode(null), ResponseDecodeError);
assert.throws(() => kernelLeakReportDecoder.decode([]), ResponseDecodeError);

// 报告里的每个枚举和后端 KernelLeakReport.cs 的枚举逐个相等（10-28 Disabled 改成 NotObserved 时前端没跟上，整份报告会解码失败）。
// 线上名是成员名，带 JsonStringEnumMemberName 的取它的值；failure 的 None 由解码器单独处理（只在不是 Unavailable 时出现）。
const backendSource = readFileSync(new URL("../../../Core/Domain/KernelLeak/KernelLeakReport.cs", import.meta.url), "utf8");
const backendEnum = (name: string): string[] => {
  // 枚举体可以写在一行里，也可以分行（KernelLeakItemState 每个成员一行）。
  const body = new RegExp(`public enum ${name}\\s*\\{([^}]*)\\}`).exec(backendSource)?.[1];
  assert.ok(body !== undefined, `backend enum ${name} exists`);
  return body.split(",").map(member => /JsonStringEnumMemberName\("([^"]+)"\)/.exec(member)?.[1] ?? member.trim()).filter(Boolean);
};
for (const [name, frontend] of [["KernelLeakReportStatus", kernelLeakReportStatuses], ["KernelLeakResourceKind", kernelLeakResourceKinds],
  ["KernelLeakUnit", kernelLeakUnits], ["KernelLeakMetadataStatus", kernelLeakMetadataStatuses], ["KernelLeakRestartPolicy", kernelLeakRestartPolicies],
  ["KernelLeakActionKind", kernelLeakActionKinds], ["KernelLeakRiskLevel", kernelLeakRiskLevels], ["KernelLeakItemState", kernelLeakItemStates]] as const) {
  assert.deepEqual([...frontend], backendEnum(name), `${name} matches the backend`);
}
assert.deepEqual(["None", ...kernelLeakFailures], backendEnum("KernelLeakFailure"), "KernelLeakFailure matches the backend");
// 观察计划还没发布时 NotObserved 的要求是 0。
assert.equal(kernelLeakReportDecoder.decode(kernelLeakReportFrames.notObservedBeforePlan).observation.requiredSampleCount, 0);

console.log(`PASS kernel leak report decoder: ${Object.keys(kernelLeakRecordedFrames).length} recorded frames, ${Object.keys(kernelLeakReportFrames).length} scenario frames, ${invalidCases.length} contract violations`);
