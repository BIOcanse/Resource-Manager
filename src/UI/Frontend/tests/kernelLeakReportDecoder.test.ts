import assert from "node:assert/strict";
import { kernelLeakReportDecoder, type KernelLeakReport } from "../src/data/kernelLeaks/kernelLeakReport.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { kernelLeakReportFrames } from "./fixtures/kernelLeakReports.js";

// 后端第 1 期给出的示例帧（kernel-leak-report 工作树 .local/reports/noisy-wire-example.json），原样解码。
const backendExample = {
  status: "Suspected",
  capturedAt: "1970-01-01T00:00:07+00:00",
  items: [{
    resourceKind: "ProcessHandles", currentValue: 1297, growthPerSecond: 42.42857142857143,
    observationSeconds: 7, sampleCount: 8, growth: 297,
    holder: {
      processId: 7, startKey: "42", name: "recorded-holder", imagePath: "C:\\recorded\\holder.exe",
      productName: "recorded-product", serviceNames: ["recorded-service"], imageStatus: "Current", serviceStatus: "Current"
    }
  }],
  failure: "None",
  unavailableHolderCount: 0
};
assert.deepEqual(kernelLeakReportDecoder.decode(backendExample), {
  status: "Suspected",
  capturedAt: "1970-01-01T00:00:07+00:00",
  unavailableHolderCount: 0,
  items: [{
    resourceKind: "ProcessHandles", currentValue: 1297, growthPerSecond: 42.42857142857143,
    observationSeconds: 7, sampleCount: 8, growth: 297,
    holder: {
      processId: 7, startKey: "42",
      image: { status: "Current", name: "recorded-holder", imagePath: "C:\\recorded\\holder.exe", productName: "recorded-product" },
      services: { status: "Current", names: ["recorded-service"] }
    }
  }]
} satisfies KernelLeakReport);

// 每个模拟场景（除了故意不合合同的那一个）都能解码，状态按场景名对应。
for (const [name, frame] of Object.entries(kernelLeakReportFrames)) {
  if (name === "invalid") {
    assert.throws(() => kernelLeakReportDecoder.decode(frame),
      (error: unknown) => error instanceof ResponseDecodeError && error.path === "$.items[0].resourceKind", name);
    continue;
  }
  const decoded = kernelLeakReportDecoder.decode(frame);
  assert.equal(decoded.status, frame.status, name);
  assert.equal(decoded.unavailableHolderCount, frame.unavailableHolderCount, name);
}

// 按状态区分：Empty 只有采集时间；Unavailable 带原因，可没有采集时间。
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakReportFrames.empty),
  { status: "Empty", capturedAt: kernelLeakReportFrames.empty.capturedAt, unavailableHolderCount: 0 });
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakReportFrames["unavailable-capacity"]),
  { status: "Unavailable", capturedAt: null, failure: "CapacityExceeded", unavailableHolderCount: 0 });
assert.deepEqual(kernelLeakReportDecoder.decode(kernelLeakReportFrames["unavailable-source"]),
  { status: "Unavailable", capturedAt: kernelLeakReportFrames["unavailable-source"].capturedAt, failure: "SourceUnavailable", unavailableHolderCount: 214 });

// 持有者信息：各自的状态不是 Current 时不带值；Current 且服务名单为空表示没有对应服务；没有产品名是 null。
const suspected = kernelLeakReportDecoder.decode(kernelLeakReportFrames.suspected);
assert.equal(suspected.status, "Suspected");
if (suspected.status !== "Suspected") throw new Error("unreachable");
const holders = suspected.items.map(item => item.holder);
assert.deepEqual(holders.map(holder => holder.image.status), ["Current", "Current", "Current", "ProcessEnded", "Unavailable", "Current"]);
assert.deepEqual(holders[3].image, { status: "ProcessEnded" });
assert.deepEqual(holders[3].services, { status: "Unavailable" });
assert.deepEqual(holders[4].image, { status: "Unavailable" });
assert.deepEqual(holders[4].services, { status: "Current", names: [] });
assert.deepEqual(holders[2].services, { status: "Current", names: ["Appinfo", "ProfSvc", "Schedule", "Winmgmt"] });
assert.equal(holders[5].image.status === "Current" ? holders[5].image.productName : "?", null);

// 进程已结束但后端仍附带了名称和路径：只按状态解码，不使用那些值。
const endedWithText = structuredClone(backendExample);
endedWithText.items[0].holder.imageStatus = "ProcessEnded";
const endedDecoded = kernelLeakReportDecoder.decode(endedWithText);
assert.deepEqual(endedDecoded.status === "Suspected" ? endedDecoded.items[0].holder.image : null, { status: "ProcessEnded" });

// 不合合同：未知枚举、缺字段、类型不对、状态与内容不一致，都在边界上报出具体路径。
type Mutation = (frame: any) => void;
const invalidCases: Array<[string, Mutation, string]> = [
  ["not an object", frame => { frame.items = undefined; frame.status = undefined; }, "$.status"],
  ["unknown status", frame => { frame.status = "Observing"; }, "$.status"],
  ["unknown failure", frame => { frame.failure = "Timeout"; }, "$.failure"],
  ["missing count", frame => { delete frame.unavailableHolderCount; }, "$.unavailableHolderCount"],
  ["negative count", frame => { frame.unavailableHolderCount = -1; }, "$.unavailableHolderCount"],
  ["time without offset", frame => { frame.capturedAt = "2026-10-27T10:42:30"; }, "$.capturedAt"],
  ["unknown resource kind", frame => { frame.items[0].resourceKind = "FileCachePoolBytes"; }, "$.items[0].resourceKind"],
  ["fractional value", frame => { frame.items[0].currentValue = 12.5; }, "$.items[0].currentValue"],
  ["text value", frame => { frame.items[0].growth = "297"; }, "$.items[0].growth"],
  ["infinite rate", frame => { frame.items[0].growthPerSecond = null; }, "$.items[0].growthPerSecond"],
  ["negative observation", frame => { frame.items[0].observationSeconds = -1; }, "$.items[0].observationSeconds"],
  ["missing sample count", frame => { delete frame.items[0].sampleCount; }, "$.items[0].sampleCount"],
  ["non-decimal start key", frame => { frame.items[0].holder.startKey = "0x2a"; }, "$.items[0].holder.startKey"],
  ["numeric start key", frame => { frame.items[0].holder.startKey = 42; }, "$.items[0].holder.startKey"],
  ["unknown image status", frame => { frame.items[0].holder.imageStatus = "Stale"; }, "$.items[0].holder.imageStatus"],
  ["unknown service status", frame => { frame.items[0].holder.serviceStatus = "Partial"; }, "$.items[0].holder.serviceStatus"],
  ["missing service list", frame => { delete frame.items[0].holder.serviceNames; }, "$.items[0].holder.serviceNames"],
  ["empty service name", frame => { frame.items[0].holder.serviceNames = [""]; }, "$.items[0].holder.serviceNames[0]"],
  ["current image without path", frame => { frame.items[0].holder.imagePath = null; }, "$.items[0].holder.imagePath"],
  ["current image without name", frame => { frame.items[0].holder.name = null; }, "$.items[0].holder.name"],
  ["suspected without items", frame => { frame.items = []; }, "$.items"],
  ["suspected with a failure", frame => { frame.failure = "SourceUnavailable"; }, "$.failure"],
  ["suspected without a time", frame => { frame.capturedAt = null; }, "$.capturedAt"],
  ["empty with items", frame => { frame.status = "Empty"; }, "$.items"],
  ["unavailable without a reason", frame => { frame.status = "Unavailable"; frame.items = []; }, "$.failure"],
  ["unavailable with items", frame => { frame.status = "Unavailable"; frame.failure = "NotObserved"; }, "$.items"]
];
for (const [name, mutate, expectedPath] of invalidCases) {
  const frame = structuredClone(backendExample) as any;
  mutate(frame);
  assert.throws(() => kernelLeakReportDecoder.decode(frame),
    (error: unknown) => error instanceof ResponseDecodeError && error.path === expectedPath,
    `${name} should fail at ${expectedPath}`);
}
assert.throws(() => kernelLeakReportDecoder.decode(null), ResponseDecodeError);
assert.throws(() => kernelLeakReportDecoder.decode([]), ResponseDecodeError);

console.log(`PASS kernel leak report decoder: ${Object.keys(kernelLeakReportFrames).length} scenario frames, ${invalidCases.length} contract violations`);
