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
  [d.decodeHostMemoryExclusion, { processId: 1, processStartKey: "0", name: "system", reason: "critical-process" }],
  [d.decodeHostMemoryProtectionStatus, f.memoryStatus]
] as const) {
  assert.deepEqual(decode(value), value);
  for (const invalid of [null, [], {}, undefined]) assert.throws(() => decode(invalid));
  for (const key of Object.keys(value)) assert.throws(() => decode({ ...value, [key]: undefined }), `required ${key}`);
}
for (const actionMode of ["dumpThenTerminate", "terminateOnly"]) {
  assert.equal(d.memoryProtectionSettingsDecoder.decode({ ...f.memorySettings, actionMode }).actionMode, actionMode);
  assert.equal(d.memoryProtectionStatusDecoder.decode({ ...f.memoryStatus, actionMode }).actionMode, actionMode);
}
for (const [outcome, reason] of [["completed", null], ["completed-with-release-limit", "private-memory-decommit-limited chunks=1 bytes=4096; address=0x7ffe0000"],
  ["slice-incomplete", "IO error"], ["preflight-failed", "Access denied"], ["suspend-failed", "ntstatus-c0000022"],
  ["termination-failed", "terminate-failed-5"], ["termination-failed", "termination-not-observed"]]) {
  assert.equal(d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome, reason }).outcome, outcome);
}
for (const outcome of ["completed", "completed-with-release-limit", "suspend-failed", "termination-failed"]) {
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome, reason: "unknown" }));
}
for (const reason of ["terminate-only", "software-no-dump", "private-commit-exceeds-reserve", "minidump-exceeds-reserve"]) {
  assert.equal(d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: "terminated-without-dump", reason }).reason, reason);
}
for (const reason of ["resource-manager-process", "critical-process", "process-identity-unavailable", "private-commit-unavailable",
  "baseline-private-commit-unavailable", "cannot-open", "identity-changed", "criticality-unavailable"]) {
  assert.equal(d.decodeHostMemoryExclusion({ processId: 1, processStartKey: "0", name: "system", reason }).reason, reason);
}
for (const inactiveReason of [null, "disabled", "no-normal-running-point", "disk-reserve-not-built"]) {
  assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, inactiveReason }).inactiveReason, inactiveReason);
}
for (const invalid of ["unknown", "DumpThenTerminate", null, 0]) {
  assert.throws(() => d.memoryProtectionSettingsDecoder.decode({ ...f.memorySettings, actionMode: invalid }));
  assert.throws(() => d.memoryProtectionStatusDecoder.decode({ ...f.memoryStatus, actionMode: invalid }));
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: invalid }));
  assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome: "terminated-without-dump", reason: invalid }));
  assert.throws(() => d.decodeHostMemoryExclusion({ processId: 1, processStartKey: "0", name: "system", reason: invalid }));
}
for (const outcome of ["excluded", "reserve-unavailable"]) assert.throws(() => d.decodeHostMemoryActionRecord({ ...f.memoryAction, outcome }));
assert.throws(() => d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, inactiveReason: "unknown" }));
const nativeReason = "Access to the path is denied; reserve-allocation-unavailable: IO error";
assert.equal(d.decodeHostMemoryProtectionStatus({ ...f.memoryStatus, inactiveReason: nativeReason,
  reserve: { ...f.memoryReserve, built: false, reason: nativeReason } }).inactiveReason, nativeReason);
assert.equal(d.nullableNormalRunningPointDecoder.decode(null), null);
assert.deepEqual(d.nullableNormalRunningPointDecoder.decode(f.memoryPoint), f.memoryPoint);
assert.deepEqual(d.normalRunningPointDecoder.decode(f.memoryPoint), f.memoryPoint);
assert.throws(() => d.normalRunningPointDecoder.decode(null));
assert.throws(() => d.nullableNormalRunningPointDecoder.decode(undefined));
assert.deepEqual(d.decodeHostMemoryProcess({ ...f.memoryProcess, privateCommitBytes: null, softwareId: null }),
  { ...f.memoryProcess, privateCommitBytes: null, softwareId: null });
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
assert.throws(() => appSettingsResultDecoder.decode({ ...f.memorySettingsResult, settings: { version: "1.0.36",
  memoryProtection: { ...f.memorySettings, actionMode: "unknown" } } }));
assert.deepEqual(softwarePolicyDecoder.decode(f.softwarePolicy), f.softwarePolicy);
for (const hostMemoryProtectionNoDump of [false, true]) assert.equal(softwarePolicyDecoder.decode({ ...f.softwarePolicy, hostMemoryProtectionNoDump }).hostMemoryProtectionNoDump, hostMemoryProtectionNoDump);
for (const hostMemoryProtectionNoDump of [null, undefined, "true", 0]) assert.throws(() => softwarePolicyDecoder.decode({ ...f.softwarePolicy, hostMemoryProtectionNoDump }));
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
