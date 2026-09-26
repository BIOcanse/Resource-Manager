import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const runtime = readSource("frontendRuntime/FrontendRuntime.ts");
const registry = readSource("frontendRuntime/task/TaskRegistry.ts");
const descriptor = readSource("frontendRuntime/task/TaskDescriptor.ts");
const resourceBreakdown = readSource("features/resourceBreakdown/ResourceBreakdown.tsx");
const app = readSource("App.tsx");
const appShell = readSource("app/AppShell.tsx");
const taskCenter = readSource("ui/task-center/TaskCenterDialog.tsx");

assert.equal((runtime.match(/new TaskRegistry\(/g) ?? []).length, 1);
assert.match(runtime, /readonly taskRegistry: TaskRegistry/);
assert.match(
  runtime,
  /taskRegistry\.dispose\(\);[\s\S]*sourceRegistry\.dispose\(\);[\s\S]*backendSession\.dispose\(\);/,
  "Frontend tasks must lose commit authority before sources and backend session are disposed.");
assert.doesNotMatch(
  registry,
  /external-durable[\s\S]{0,240}executor\(/,
  "External durable tasks must remain projections instead of frontend executors.");
assert.match(registry, /maxTerminalEntries/);
assert.match(registry, /terminalRetentionMs/);
assert.match(registry, /queueMicrotask/);
assert.match(registry, /exclusiveBySlot/);
assert.match(registry, /unsettledBySlot/);
assert.match(registry, /kind: "remove"/);
assert.match(descriptor, /defineTaskKey<T>/);
assert.match(descriptor, /readonly \[taskResultType\]: \(value: T\) => T/);
assert.match(resourceBreakdown, /createResourceLayoutSettleController/);
assert.match(resourceBreakdown, /props\.animationMode/);
assert.match(resourceBreakdown, /<For each=\{snapshotBarIds\(\)\}>/);
assert.doesNotMatch(resourceBreakdown, /<For each=\{props\.snapshotBars\}>/);
assert.doesNotMatch(resourceBreakdown, /document\.body\.dataset\.animations === "normal"/);
assert.match(app, /animationMode=\{gpuRuntimePlan\(\)\.animations\}/);
assert.match(app, /frontendRuntime\.taskCenter\.subscribe/);
assert.match(app, /<TaskCenterDialog/);
assert.match(appShell, /id="taskCenterButton"/);
assert.match(taskCenter, /<DialogRoot/);
assert.match(taskCenter, /<TabsRoot/);
assert.match(taskCenter, /props\.items\.map\(\(item\) => item\.id\)/);
assert.match(taskCenter, /data-task-center-id=\{itemId\}/);
assert.match(taskCenter, /<strong>\{item\(\)\.title\}<\/strong>/);
assert.match(taskCenter, /taskCancelLabel\(item\(\)\)/);
assert.doesNotMatch(taskCenter, /setInterval|setTimeout|fetch\(/);
assert.doesNotMatch(
  resourceBreakdown,
  /setTimeout\([^)]*440|moveTimer/,
  "ResourceBreakdown must not retain a second timer owner for layout settlement.");

function readSource(relativePath: string): string {
  return readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");
}
