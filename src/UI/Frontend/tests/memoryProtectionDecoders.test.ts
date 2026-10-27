import assert from "node:assert/strict";
import * as d from "../src/data/memoryProtection/memoryProtectionDecoders.ts";
import { appSettingsResultDecoder } from "../src/data/appSettings/appSettingsResultDecoder.ts";
import { softwarePolicyDecoder } from "../src/data/softwarePolicy/softwarePolicyDecoder.ts";
import * as f from "./fixtures/memoryProtection.ts";

for (const [decode, value] of [
  [d.decodeHostMemoryProtectionSettings, f.memorySettings], [d.decodeHostMemoryCommit, f.memoryCommit],
  [d.decodeHostMemoryProcess, f.memoryProcess], [d.decodeNormalRunningPointSummary, f.memoryStatus.normalRunningPoint],
  [d.decodeNormalRunningPoint, f.memoryPoint], [d.decodeHostMemoryReserveEntryStatus, f.memoryReserveEntry],
  [d.decodeHostMemoryReserveStatus, f.memoryReserve], [d.decodeHostMemoryActionRecord, f.memoryAction],
  [d.decodeHostMemoryActionRecord, f.memoryTreeChild], [d.decodeHostMemoryActionRecord, f.memoryTreeRootRecord],
  [d.decodeHostMemoryTreeRoot, f.memoryTreeRoot],
  [d.decodeHostMemoryExclusion, { processId: 1, processStartKey: "0", name: "system", reason: "critical-process" }],
  [d.decodeHostMemoryProtectionStatus, f.memoryStatus], [d.decodeHostMemoryProtectionLiveStatus, f.memoryLiveStatus],
  [d.decodeHostMemoryProtectionFrame, { type: "status", status: f.memoryLiveStatus }],
  [d.decodeHostMemoryProtectionFrame, { type: "records", records: [f.memoryAction, f.memoryTreeChild], limit: 64 }],
  [d.decodeHostMemoryProtectionFrame, { type: "record", record: f.memoryTreeRootRecord }],
  [d.decodeHostMemoryProtectionFrame, { type: "normalRunningPoint", normalRunningPoint: f.memoryPoint }]
] as const) {
  assert.deepEqual(decode(value), value);
  for (const invalid of [null, [], {}, undefined]) assert.throws(() => decode(invalid));
  for (const key of Object.keys(value)) assert.throws(() => decode({ ...value, [key]: undefined }), `required ${key}`);
}
for (const actionMode of ["dumpThenTerminate", "terminateOnly"]) {
  assert.equal(d.memoryProtectionSettingsDecoder.decode({ ...f.memorySettings, actionMode }).actionMode, actionMode);
  assert.equal(d.memoryProtectionStatusDecoder.decode({ ...f.memoryStatus, actionMode }).actionMode, actionMode);
}
// WindowsHostMemoryProtectionExecutor: one stable code per outcome; native and IO text only in detail, sent as is.
for (const [outcome, reason, detail] of [["completed-with-release-limit", "release-limit", "private-memory-decommit-limited chunks=1 bytes=4096; address=0x7ffe0000"],
  ["slice-incomplete", "slice-incomplete", "IO error"], ["preflight-failed", "preflight-failed", "Access denied"],
  ["suspend-failed", "suspend-failed", "ntstatus-c0000022"], ["termination-failed", "termination-failed", "terminate-failed-5"],
  ["termination-failed", "termination-failed", "termination-not-observed"],
  ["terminated-without-dump", "reserve-error", "Access to the reserve file was denied."],
  ["terminated-without-dump", "reserve-error", null]]) {
  const decoded = d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome, reason, detail });
  assert.deepEqual([decoded.outcome, decoded.reason, decoded.detail], [outcome, reason, detail]);
}
for (const [outcome, reason] of [["completed", "release-limit"], ["completed-with-release-limit", "private-memory-decommit-limited chunks=1 bytes=4096"],
  ["suspend-failed", "ntstatus-c0000022"], ["termination-failed", "terminate-failed-5"], ["slice-incomplete", "IO error"],
  ["preflight-failed", "suspend-failed"], ["terminated-without-dump", "reserve-release-failed"],
  ["terminated-without-dump", "Access to the path is denied."], ["suspend-failed", null]]) {
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome, reason }), `${outcome} with ${reason}`);
}
assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, detail: "unexpected" }), "a completed dump has no detail");
assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: "slice-incomplete", reason: "slice-incomplete", detail: 5 }));
for (const reason of ["terminate-only", "software-no-dump", "private-commit-exceeds-reserve", "minidump-exceeds-reserve",
  "process-tree", "reserve-unavailable", "reserve-error"]) {
  assert.equal(d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: "terminated-without-dump", reason }).reason, reason);
}
assert.deepEqual([...d.memoryProtectionActionReasons].sort(), ["minidump-exceeds-reserve", "preflight-failed", "private-commit-exceeds-reserve",
  "process-tree", "release-limit", "reserve-error", "reserve-unavailable", "slice-incomplete", "software-no-dump", "suspend-failed",
  "terminate-only", "termination-failed"]);
// The selection basis names the rule of the action's target kind and fills exactly that rule's ranking value.
assert.deepEqual(d.decodeHostMemoryActionRecord({ ...f.memoryAction, selectionBasis: { ...f.memoryProcessBasis, processGrowthBytes: -5 } })
  .selectionBasis.processGrowthBytes, -5);
for (const [record, selectionBasis] of [
  [f.memoryAction, f.memoryTreeBasis], [f.memoryTreeChild, f.memoryProcessBasis],
  [f.memoryAction, { ...f.memoryProcessBasis, processGrowthBytes: null }],
  [f.memoryAction, { ...f.memoryProcessBasis, directChildrenMemoryGrowthBytes: 1 }],
  [f.memoryTreeChild, { ...f.memoryTreeBasis, processGrowthBytes: 1 }],
  [f.memoryTreeChild, { ...f.memoryTreeBasis, directChildrenMemoryGrowthBytes: null }],
  [f.memoryAction, { ...f.memoryProcessBasis, rule: "unknown" }],
  [f.memoryAction, { ...f.memoryProcessBasis, minimumProcessGrowthBytes: 1.5 }],
  [f.memoryAction, { ...f.memoryProcessBasis, minimumProcessGrowthBytes: "512" }],
  [f.memoryAction, null]
] as const) {
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...record, selectionBasis }), JSON.stringify(selectionBasis));
}
// Frames dispatch on type; the status frame has no records or point, a cleared point is null.
assert.equal(d.memoryProtectionFrameDecoder.decode({ type: "normalRunningPoint", normalRunningPoint: null }).type, "normalRunningPoint");
assert.deepEqual(d.memoryProtectionFrameDecoder.decode({ type: "records", records: [], limit: 0 }), { type: "records", records: [], limit: 0 });
for (const invalid of [{ type: "status" }, { type: "unknown" }, { status: f.memoryLiveStatus },
  { type: "records", records: [], limit: -1 }, { type: "records", records: [] }, { type: "record", record: null },
  { type: "normalRunningPoint" }, { type: "normalRunningPoint", normalRunningPoint: { at: f.memoryPoint.at } }]) {
  assert.throws(() => d.memoryProtectionFrameDecoder.decode(invalid), JSON.stringify(invalid).slice(0, 80));
}
const exclusionReasons = ["resource-manager-process", "critical-process", "process-identity-unavailable", "private-commit-unavailable",
  "baseline-private-commit-unavailable", "cannot-open", "identity-changed", "criticality-unavailable", "protected-process", "software-never-terminate"];
for (const reason of exclusionReasons) {
  assert.equal(d.decodeHostMemoryExclusion({ processId: 1, processStartKey: "0", name: "system", reason }).reason, reason);
  assert.equal(d.decodeHostMemoryActionRecord({ ...f.memoryTreeRootRecord, reason }).reason, reason, `skipped tree member: ${reason}`);
}
assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryTreeRootRecord, reason: "process-tree" }), "a skip carries an exclusion reason");
assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryTreeRootRecord, reason: null }));
// Skipped members can have unreadable memory: null is kept, not replaced.
const unreadable = { ...f.memoryTreeRootRecord, baselineBytes: null, currentBytes: null, growthBytes: null, reason: "software-never-terminate" };
assert.deepEqual(d.decodeHostMemoryActionRecord(unreadable), unreadable);
// An exited root keeps its PID; start key and name may be null.
const exitedRoot = { ...f.memoryTreeChild, treeRoot: { processId: 7, processStartKey: null, name: null } };
assert.deepEqual(d.decodeHostMemoryActionRecord(exitedRoot), exitedRoot);
for (const [targetKind, treeRoot] of [["process", f.memoryTreeRoot], ["processTree", null], ["tree", null], ["processTree", {}]] as const) {
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, targetKind, treeRoot }), `${targetKind} with ${JSON.stringify(treeRoot)}`);
}
for (const actionId of ["", "  ", null, 1]) assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, actionId }));
for (const decisionReason of [null, "commit-unavailable", "below-threshold", "process-handled", "process-tree-handled",
  "no-positive-child-memory-growth", "no-eligible-tree"]) {
  assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, decisionReason }).decisionReason, decisionReason);
}
assert.throws(() => d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, decisionReason: "unknown" }));
for (const atOrAboveThreshold of [null, true, false]) {
  assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, atOrAboveThreshold }).atOrAboveThreshold, atOrAboveThreshold);
}
assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, currentActionId: f.memoryAction.actionId }).currentActionId, f.memoryAction.actionId);
// active is the backend's "inactiveReason is null"; protection stays active while the reserve is not built after a run-time error.
const runtimeReserveError = { ...f.memoryStatus, active: true, inactiveReason: null,
  reserve: { ...f.memoryReserve, built: false, reason: "reserve-release-failed" } };
assert.deepEqual(d.decodeHostMemoryProtectionStatus(runtimeReserveError), runtimeReserveError);
assert.throws(() => d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, active: true }), "active with an inactive reason");
assert.throws(() => d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, active: false, inactiveReason: null }));
assert.deepEqual(d.decodeHostMemoryProtectionSettings({ ...f.memorySettings, protectedProcesses: [] }).protectedProcesses, [],
  "an explicitly empty whitelist is a valid setting");
for (const invalid of ["1073741824", 1.5, null, NaN]) {
  assert.throws(() => d.decodeHostMemoryProtectionSettings({ ...f.memorySettings, minimumProcessGrowthBytes: invalid }));
}
for (const invalid of [null, "explorer.exe", [1]]) {
  assert.throws(() => d.decodeHostMemoryProtectionSettings({ ...f.memorySettings, protectedProcesses: invalid }));
}
for (const inactiveReason of [null, "disabled", "no-normal-running-point", "disk-reserve-not-built"]) {
  assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, inactiveReason, active: inactiveReason === null }).inactiveReason, inactiveReason);
}
for (const invalid of ["unknown", "DumpThenTerminate", null, 0]) {
  assert.throws(() => d.memoryProtectionSettingsDecoder.decode({ ...f.memorySettings, actionMode: invalid }));
  assert.throws(() => d.memoryProtectionStatusDecoder.decode({ ...f.memoryStatus, actionMode: invalid }));
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: invalid }));
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: "terminated-without-dump", reason: invalid }));
  assert.throws(() => d.decodeHostMemoryExclusion({ processId: 1, processStartKey: "0", name: "system", reason: invalid }));
}
assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: "reserve-unavailable" }));
assert.throws(() => d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, inactiveReason: "unknown" }));
const nativeReason = "Access to the path is denied; reserve-allocation-unavailable: IO error";
assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, inactiveReason: nativeReason,
  reserve: { ...f.memoryReserve, built: false, reason: nativeReason } }).inactiveReason, nativeReason);
assert.deepEqual(d.normalRunningPointDecoder.decode(f.memoryPoint), f.memoryPoint);
assert.throws(() => d.normalRunningPointDecoder.decode(null));
assert.deepEqual(d.decodeHostMemoryProcess({ ...f.memoryProcess, privateCommitBytes: null, softwareId: null, executablePath: null, parentProcessId: null }),
  { ...f.memoryProcess, privateCommitBytes: null, softwareId: null, executablePath: null, parentProcessId: null });
assert.equal(d.decodeHostMemoryProtectionSettings({ ...f.memorySettings, reserves: null }).reserves, null);
const empty = { ...f.memoryStatus, triggerBytes: null, remainingBytes: null, currentCommit: null, normalRunningPoint: null,
  excludedCandidates: [], actions: [] };
assert.deepEqual(d.memoryProtectionStatusDecoder.decode(empty), empty);
assert.equal(d.memoryProtectionStatusDecoder.decode({ ...f.memoryStatus, remainingBytes: -123.5 }).remainingBytes, -123.5);
assert.equal(d.decodeHostMemoryActionRecord({ ...f.memoryAction, growthBytes: -20 }).growthBytes, -20);
assert.equal(d.decodeHostMemoryCommit({ ...f.memoryCommit, currentBytes: 2 ** 60 }).currentBytes, 2 ** 60);
for (const invalid of ["67108864", -1, 1.5, 2 ** 65, NaN, Infinity]) {
  assert.throws(() => d.decodeHostMemoryReserveEntryStatus({ ...f.memoryReserveEntry, remainingBytes: invalid }));
  assert.throws(() => d.decodeHostMemoryCommit({ ...f.memoryCommit, currentBytes: invalid }));
}
assert.equal(appSettingsResultDecoder.decode(f.memorySettingsResult).settings.memoryProtection?.actionMode, "dumpThenTerminate");
assert.equal(appSettingsResultDecoder.decode(f.memorySettingsResult).updatedAt, f.memoryPoint.at);
assert.throws(() => appSettingsResultDecoder.decode({ ...f.memorySettingsResult, settings: { version: "1.0.37",
  memoryProtection: { ...f.memorySettings, actionMode: "unknown" } } }));
// The settings owner's type rejection names the rejected value's path (RuntimeSpecializationCoordinator).
const typeRejection = appSettingsResultDecoder.decode({ ...f.memorySettingsResult, runtimeApplicationDisposition: "rejectedBeforeCommit",
  runtimeFailureCode: "invalid-settings-value-type", runtimeFailurePath: "$.debug.debugLogEnabled" });
assert.deepEqual([typeRejection.runtimeFailureCode, typeRejection.runtimeFailurePath], ["invalid-settings-value-type", "$.debug.debugLogEnabled"]);
assert.equal(appSettingsResultDecoder.decode(f.memorySettingsResult).runtimeFailurePath, undefined);
assert.equal(appSettingsResultDecoder.decode({ ...f.memorySettingsResult, runtimeFailurePath: null }).runtimeFailurePath, null);
assert.throws(() => appSettingsResultDecoder.decode({ ...f.memorySettingsResult, runtimeFailurePath: 1 }));
assert.deepEqual(softwarePolicyDecoder.decode(f.softwarePolicy), f.softwarePolicy);
for (const hostMemoryProtectionNoDump of [false, true]) assert.equal(softwarePolicyDecoder.decode({ ...f.softwarePolicy, hostMemoryProtectionNoDump }).hostMemoryProtectionNoDump, hostMemoryProtectionNoDump);
for (const hostMemoryProtectionNoDump of [null, undefined, "true", 0]) assert.throws(() => softwarePolicyDecoder.decode({ ...f.softwarePolicy, hostMemoryProtectionNoDump }));
for (const hostMemoryProtectionNeverTerminate of [false, true]) {
  assert.equal(softwarePolicyDecoder.decode({ ...f.softwarePolicy, hostMemoryProtectionNeverTerminate }).hostMemoryProtectionNeverTerminate,
    hostMemoryProtectionNeverTerminate);
}
for (const hostMemoryProtectionNeverTerminate of [null, undefined, "true", 0]) {
  assert.throws(() => softwarePolicyDecoder.decode({ ...f.softwarePolicy, hostMemoryProtectionNeverTerminate }));
}
for (const [key, value] of [["preferredRuntimeSwitchMethod", "unknown"], ["cpuMaximumOccupancyMode", "unknown"],
  ["startupTargetGpu", "unknown"], ["allowedProviders", ["unknown"]]]) assert.throws(() => softwarePolicyDecoder.decode({ ...f.softwarePolicy, [key]: value }));
for (const [key, values] of [
  ["preferredRuntimeSwitchMethod", ["future_frame_takeover", "window_rerender"]],
  ["cpuMaximumOccupancyMode", ["single_ccd", "all_cores"]],
  ["startupTargetGpu", ["system_default_gpu", "auto_idle_gpu", "integrated_gpu", "high_performance_gpu", "gpu0", "gpu12"]]
] as const) for (const value of values) assert.equal(softwarePolicyDecoder.decode({ ...f.softwarePolicy, [key]: value })[key], value);
const providers = ["windows-graphics-preference", "vendor-profile", "dxgi-launch-shim", "d3d-device-create-shim",
  "vulkan-implicit-layer", "vulkan-explicit-layer", "compute-visibility"];
assert.deepEqual(softwarePolicyDecoder.decode({ ...f.softwarePolicy, allowedProviders: providers }).allowedProviders, providers);
for (const startupTargetGpu of ["GPU0", "gpu-1", "gpu2147483648"]) {
  assert.throws(() => softwarePolicyDecoder.decode({ ...f.softwarePolicy, startupTargetGpu }));
}
console.log("memory protection decoders: passed");
