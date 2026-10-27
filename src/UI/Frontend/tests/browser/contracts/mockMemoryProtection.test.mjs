import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { memoryProtectionScenarios } from "../memoryProtectionFixture.mjs";
import {
  memoryProtectionFrameDecoder, memoryProtectionStatusDecoder, normalRunningPointDecoder
} from "../../../src/data/memoryProtection/memoryProtectionDecoders.ts";
import { appSettingsResultDecoder } from "../../../src/data/appSettings/appSettingsResultDecoder.ts";
import { softwarePolicyDecoder } from "../../../src/data/softwarePolicy/softwarePolicyDecoder.ts";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });
const body = (method, value) => ({ method, headers: { "content-type": "application/json" }, body: JSON.stringify(value) });
const decodePoint = point => point === null ? null : normalRunningPointDecoder.decode(point);
const protection = [{ id: "protection", path: "/api/memory-protection/subscribe" }];
const typeRejection = (result, path) => {
  assert.equal(result.status, 409, path);
  assert.equal(result.value.runtimeApplicationDisposition, "rejectedBeforeCommit");
  assert.equal(result.value.runtimeFailureCode, "invalid-settings-value-type");
  assert.equal(result.value.runtimeFailurePath, path);
  assert.equal(appSettingsResultDecoder.decode(result.value).runtimeFailurePath, path);
};
async function patch(changes, revision) {
  const initial = (await server.json("/api/settings/app")).value;
  return server.json("/api/memory-protection", body("PATCH", { expectedRevision: revision ?? initial.revision, changes: { memoryProtection: changes } }));
}
test("all named scenarios expose complete exact wire statuses and points", async () => {
  for (const name of memoryProtectionScenarios) {
    await server.scenario({ memoryProtection: name });
    const { status, value } = await server.json("/api/memory-protection");
    assert.equal(status, 200, name);
    assert.deepEqual(memoryProtectionStatusDecoder.decode(value), value, name);
    const point = (await server.json("/api/memory-protection/normal-running-point")).value;
    assert.deepEqual(decodePoint(point), point);
    if (name === "no-point") { assert.equal(point, null); assert.equal(value.inactiveReason, "no-normal-running-point"); }
    if (name === "disabled-ready") assert.equal(value.inactiveReason, "disabled");
    if (name === "active") { assert.equal(value.inactiveReason, null); assert.equal(value.remainingBytes, 750);
      assert.equal(value.reserve.remainingBytes, 67239936); assert.deepEqual(value.reserve.entries.map(entry => entry.clusterBytes), [4096, 65536]); }
    if (name === "commit-unavailable") for (const key of ["currentCommit", "remainingBytes", "triggerBytes"]) assert.equal(value[key], null);
    if (name === "reserve-consumed") { assert.equal(value.reserve.built, true); assert.equal(value.reserve.remainingBytes, 0); }
    if (name === "reserve-failed") assert.equal(value.reserve.reason, "insufficient-disk-space-for-reserve");
    if (name === "reserve-partial-cluster") { assert.equal(value.reserve.entries[1].clusterBytes, 65536); assert.match(value.reserve.reason, /cluster-bytes=65536/); }
  }
  await assert.rejects(() => server.scenario({ memoryProtection: "unknown" }));
});
test("enable refuses missing point, missing reserve and a failed reserve before commit in both modes", async () => {
  for (const actionMode of ["dumpThenTerminate", "terminateOnly"]) {
    for (const [scenario, reason] of [["no-point", "no-normal-running-point"], ["no-reserve", "reserve-not-configured"],
      ["reserve-failed", "insufficient-disk-space-for-reserve"]]) {
      await server.scenario({ memoryProtection: scenario });
      const initial = (await server.json("/api/settings/app")).value;
      const result = await patch({ enabled: true, actionMode });
      assert.equal(result.status, 409); assert.equal(result.value.runtimeFailureCode, reason);
      assert.equal(result.value.runtimeApplicationDisposition, "rejectedBeforeCommit");
      assert.equal(result.value.revision, initial.revision); assert.deepEqual(result.value.settings, initial.settings);
      appSettingsResultDecoder.decode(result.value);
    }
  }
});
test("point replacement, settings patch, disabled preparation and reserve rebuild retain backend semantics", async () => {
  await server.scenario({ memoryProtection: "no-point" });
  assert.equal((await server.json("/api/memory-protection/normal-running-point")).value, null);
  const first = await server.json("/api/memory-protection/normal-running-point", { method: "POST" });
  const second = await server.json("/api/memory-protection/normal-running-point", { method: "POST" });
  assert.equal(first.status, 200); assert.notEqual(second.value.at, first.value.at);
  assert.deepEqual((await server.json("/api/memory-protection/normal-running-point")).value, second.value);
  assert.equal(second.value.processes[0].softwareId, "frontend-harness.software");
  assert.equal(second.value.processes[1].privateCommitBytes, null);
  const reserves = [{ location: "F:\\MockReserve", sizeBytes: 67108864 }];
  const built = await patch({ reserves });
  assert.equal(built.status, 200); assert.equal(built.value.settings.version, "1.0.37");
  assert.equal((await server.json("/api/memory-protection")).value.reserve.built, true);
  assert.equal((await patch({ enabled: true, thresholdPercent: 17 })).status, 200);
  const negative = (await server.json("/api/memory-protection")).value;
  assert.equal(negative.triggerBytes, 170); assert.equal(negative.remainingBytes, -30);
  for (const [scenario, expected] of [["no-reserve", 409], ["reserve-failed", 409], ["reserve-incomplete", 200], ["reserve-building", 200], ["reserve-consumed", 200]]) {
    await server.scenario({ memoryProtection: scenario });
    const result = await server.json("/api/memory-protection/reserve/rebuild", { method: "POST" });
    assert.equal(result.status, expected, scenario); memoryProtectionStatusDecoder.decode(result.value);
    if (expected === 200) assert.equal(result.value.reserve.remainingBytes, result.value.reserve.configuredSizeBytes);
  }
});
test("semantic failures, revision conflicts and dedicated route shape never overwrite current settings", async () => {
  await server.scenario({ memoryProtection: "disabled-ready" });
  const initial = (await server.json("/api/settings/app")).value;
  for (const [changes, reason] of [[{ actionMode: "unknown" }, "invalid-action-mode"],
    [{ thresholdPercent: 0 }, "invalid-threshold-percent"], [{ checkIntervalSeconds: 0.5 }, "invalid-check-interval"],
    [{ reserves: [{ location: "", sizeBytes: 4096 }] }, "invalid-reserve-location"],
    [{ reserves: [{ location: "F:\\reserve", sizeBytes: 0 }] }, "invalid-reserve-size"],
    [{ reserves: [{ location: "F:\\reserve", sizeBytes: 4096 }, { location: "f:\\reserve", sizeBytes: 4096 }] }, "duplicate-reserve-location"],
    [{ reserves: [{ location: "G:\\reserve", sizeBytes: 4096 }] }, "reserve-size-must-be-whole-clusters:cluster-bytes=65536"],
    [{ enabled: true, reserves: [{ location: "F:\\other", sizeBytes: 4096 }] }, "disk-reserve-not-built"]]) {
    const result = await patch(changes); assert.equal(result.status, 409); assert.equal(result.value.runtimeFailureCode, reason);
    assert.equal(result.value.revision, initial.revision);
  }
  const conflict = await patch({ enabled: true }, "stale");
  assert.equal(conflict.status, 409); assert.equal(conflict.value.runtimeApplicationDisposition, "revisionConflict");
  for (const changes of [{}, { debug: {} }, { memoryProtection: {}, debug: {} }]) {
    assert.equal((await server.json("/api/memory-protection", body("PATCH", { expectedRevision: initial.revision, changes }))).status, 400);
  }
  for (const [changes, path] of [[{ memoryProtection: null }, "$.memoryProtection"],
    [{ memoryProtection: { actionMode: null } }, "$.memoryProtection.actionMode"],
    [{ memoryProtection: { reserves: null } }, "$.memoryProtection.reserves"],
    [{ memoryProtection: { unknown: true } }, "$.memoryProtection.unknown"]]) {
    const rejected = await server.json("/api/memory-protection", body("PATCH", { expectedRevision: initial.revision, changes }));
    typeRejection(rejected, path);
    assert.equal(rejected.value.revision, initial.revision);
  }
  const nullModePut = await server.json("/api/settings/app", body("PUT", { ...initial.settings,
    memoryProtection: { ...initial.settings.memoryProtection, actionMode: null } }));
  assert.equal(nullModePut.status, 409); assert.equal(nullModePut.value.runtimeFailureCode, "invalid-action-mode");
  assert.deepEqual((await server.json("/api/settings/app")).value.settings, initial.settings);
  for (const disposition of ["committedAndApplied", "committedWithCapabilityConstraints", "committedWithDeliveryFailures", "savedNotApplied"]) {
    await server.scenario({ settingsRuntimeDisposition: disposition });
    const result = await patch({ checkIntervalSeconds: 2 });
    assert.equal(result.status, ["committedWithDeliveryFailures", "savedNotApplied"].includes(disposition) ? 202 : 200);
    assert.equal(result.value.runtimeApplicationDisposition, disposition);
  }
});
test("every recorded outcome and direct-action reason is available; exclusions are separate", async () => {
  await server.scenario({ memoryProtection: "all-outcomes" });
  const status = memoryProtectionStatusDecoder.decode((await server.json("/api/memory-protection")).value);
  assert.deepEqual([...new Set(status.actions.map(row => row.outcome))].sort(), ["completed", "completed-with-release-limit", "preflight-failed",
    "slice-incomplete", "suspend-failed", "terminated-without-dump", "termination-failed"].sort());
  assert.deepEqual(status.actions.map(row => [row.outcome, row.reason, row.detail !== null]).reverse(), [
    ["completed", null, false], ["completed-with-release-limit", "release-limit", true], ["preflight-failed", "preflight-failed", true],
    ["suspend-failed", "suspend-failed", true], ["slice-incomplete", "slice-incomplete", true],
    ["termination-failed", "termination-failed", true], ["terminated-without-dump", "terminate-only", false],
    ["terminated-without-dump", "software-no-dump", false], ["terminated-without-dump", "private-commit-exceeds-reserve", false],
    ["terminated-without-dump", "minidump-exceeds-reserve", false], ["terminated-without-dump", "reserve-error", true]],
    "a stable code per outcome, diagnostic text only in detail");
  assert(status.actions.every(row => row.selectionBasis.rule === "process-growth-at-least-minimum"
    && row.selectionBasis.processGrowthBytes === 700 && row.selectionBasis.directChildrenMemoryGrowthBytes === null));
  assert.equal(status.excludedCandidates.length, 10); assert.equal(status.actionRecordLimit, 64);
  assert.equal(new Set(status.actions.map(row => row.actionId)).size, status.actions.length, "each single-process action has its own id");
  for (const action of status.actions.filter(row => row.outcome === "terminated-without-dump")) {
    assert.deepEqual(action.dumpPaths, []); assert.equal(action.bytesWritten, 0);
  }
  assert.equal(status.actions.find(row => row.outcome === "slice-incomplete").dumpPaths.length, 2);
});
test("full software policy flag round trip survives unrelated edits across all publication results", async () => {
  const id = "frontend-harness.software";
  const path = `/api/gpu-placement/software/${id}`;
  for (const [disposition, code] of [["committedAndPublished", 200], ["committedWithDeliveryFailures", 202], ["savedNotApplied", 409]]) {
    await server.scenario({ runtimePolicyDisposition: disposition });
    const defaults = (await server.json(`${path}/policy/default?softwareName=Example`)).value;
    assert.equal(softwarePolicyDecoder.decode(defaults).hostMemoryProtectionNoDump, false);
    const draft = { ...defaults, softwareId: "ignored-body-id", hostMemoryProtectionNoDump: true };
    const saved = await server.json(`${path}/policy`, body("PUT", draft));
    assert.equal(saved.status, code); assert.equal(saved.value.policy.softwareId, id);
    assert.equal(saved.value.runtimeApplicationDisposition, disposition);
    const loaded = softwarePolicyDecoder.decode((await server.json(path)).value.softwarePolicy);
    assert.equal(loaded.hostMemoryProtectionNoDump, true);
    assert.equal(loaded.hostMemoryProtectionNeverTerminate, false);
    const neverTerminate = await server.json(`${path}/policy`, body("PUT", { ...loaded, hostMemoryProtectionNeverTerminate: true }));
    assert.equal(neverTerminate.value.policy.hostMemoryProtectionNeverTerminate, true);
    const edited = { ...neverTerminate.value.policy, cpuSchedulingExcluded: !loaded.cpuSchedulingExcluded };
    const unrelated = await server.json(`${path}/policy`, body("PUT", edited));
    assert.equal(unrelated.value.policy.hostMemoryProtectionNoDump, true);
    assert.equal(unrelated.value.policy.hostMemoryProtectionNeverTerminate, true);
    assert.equal(unrelated.value.policy.cpuSchedulingExcluded, edited.cpuSchedulingExcluded);
    assert.equal((await server.json(path)).value.softwarePolicy.hostMemoryProtectionNoDump, true);
  }
});
test("the subscription replays status, records and point, then sends one frame per change", async () => {
  await server.scenario({ memoryProtection: "tree-actions" });
  const expected = (await server.json("/api/memory-protection")).value;
  const replay = (await server.frames(protection, frames => frames.length >= 3)).map(frame => frame.value);
  assert.deepEqual(replay.map(frame => frame.type), ["status", "records", "normalRunningPoint"], "replay order");
  for (const frame of replay) assert.deepEqual(memoryProtectionFrameDecoder.decode(frame), frame);
  const { normalRunningPoint, actionRecordLimit, actions, ...live } = expected;
  assert.deepEqual(replay[0].status, live, "the status frame carries no records and no point");
  assert.deepEqual(replay[1], { type: "records", records: actions, limit: actionRecordLimit });
  assert.deepEqual(replay[2].normalRunningPoint, (await server.json("/api/memory-protection/normal-running-point")).value);
  assert.equal(replay[2].normalRunningPoint.processCount, normalRunningPoint.processCount);
  const status = replay[0].status;
  assert.equal(status.active, true); assert.equal(status.reserve.built, false, "a run-time reserve error keeps protection active");
  assert.equal(status.atOrAboveThreshold, null); assert.equal(status.decisionReason, "commit-unavailable");
  const tree = actions.filter(row => row.targetKind === "processTree");
  assert.equal(new Set(tree.map(row => row.actionId)).size, 1);
  assert.deepEqual(tree.map(row => row.outcome), ["excluded", "excluded", "terminated-without-dump", "terminated-without-dump"]);
  assert(tree.every(row => row.treeRoot?.processId === 300));
  assert(tree.every(row => JSON.stringify(row.selectionBasis) === JSON.stringify({ rule: "largest-direct-children-memory-growth",
    processGrowthBytes: null, directChildrenMemoryGrowthBytes: 30, minimumProcessGrowthBytes: 1073741824 })), "one basis per action");
  assert.equal(tree.find(row => row.reason === "software-never-terminate").currentBytes, null, "a skipped member may have no memory value");
  const single = actions.find(row => row.targetKind === "process");
  assert.deepEqual([single.reason, single.detail], ["reserve-error", "reserve-release-failed"]);

  const changed = server.frames(protection, frames => frames.some(frame => frame.value.type === "status"
    && frame.value.status.decisionReason === "no-eligible-tree"));
  await new Promise(resolve => setTimeout(resolve, 200));
  await server.scenario({ memoryProtectionDecision: { decisionReason: "no-eligible-tree" } });
  const after = (await changed).map(frame => frame.value);
  assert.deepEqual(after.slice(3).map(frame => frame.type), ["status"], "a decision is one status frame");
  await assert.rejects(() => server.scenario({ memoryProtectionDecision: { decisionReason: "unknown" } }));

  const events = server.frames(protection, frames => frames.filter(frame => frame.value.type === "normalRunningPoint").length >= 2);
  await new Promise(resolve => setTimeout(resolve, 200));
  await server.scenario({ memoryProtectionRecords: [{ actionId: "1".repeat(32), processId: 61, outcome: "terminated-without-dump",
    reason: "terminate-only" }], memoryProtectionClearPoint: true });
  const tail = (await events).map(frame => frame.value).slice(3);
  assert.deepEqual(tail.map(frame => frame.type), ["record", "status", "normalRunningPoint"]);
  assert.equal(tail[0].record.processId, 61); memoryProtectionFrameDecoder.decode(tail[0]);
  assert.equal(tail[1].status.inactiveReason, "no-normal-running-point");
  assert.equal(tail[2].normalRunningPoint, null, "a cleared point is null");
  const created = server.frames(protection, frames => frames.filter(frame => frame.value.type === "normalRunningPoint").length >= 2);
  await new Promise(resolve => setTimeout(resolve, 200));
  const point = (await server.json("/api/memory-protection/normal-running-point", { method: "POST" })).value;
  assert.deepEqual((await created).at(-1).value, { type: "normalRunningPoint", normalRunningPoint: point }, "creation sends the full point");
});
test("record frames upsert by action and process identity and the replay honours the limit", async () => {
  await server.scenario({ memoryProtection: "active", memoryProtectionRecordLimit: 2 });
  await server.scenario({ memoryProtectionRecords: [{ actionId: "a".repeat(32), processId: 1 }, { actionId: "b".repeat(32), processId: 2 },
    { actionId: "c".repeat(32), processId: 3 }, { actionId: "b".repeat(32), processId: 2, outcome: "termination-failed",
      reason: "termination-failed", detail: "terminate-failed-5" }] });
  const { actions, actionRecordLimit } = (await server.json("/api/memory-protection")).value;
  assert.equal(actionRecordLimit, 2);
  assert.deepEqual(actions.map(row => [row.processId, row.outcome]), [[3, "terminated-without-dump"], [2, "termination-failed"]]);
  const replay = (await server.frames(protection, frames => frames.length >= 3)).map(frame => frame.value);
  assert.deepEqual(replay[1], { type: "records", records: actions, limit: 2 });
});
test("the minimum growth and whitelist settings save as sent and reject with the backend codes", async () => {
  await server.scenario({ memoryProtection: "disabled-ready", settingsRuntimeDisposition: "committedAndApplied" });
  const defaults = (await server.json("/api/settings/app")).value.settings.memoryProtection;
  assert.equal(defaults.minimumProcessGrowthBytes, 1073741824); assert.deepEqual(defaults.protectedProcesses, ["explorer.exe"]);
  const saved = await patch({ minimumProcessGrowthBytes: 536870912, protectedProcesses: [] });
  assert.equal(saved.status, 200);
  assert.deepEqual(saved.value.settings.memoryProtection.protectedProcesses, [], "an explicitly empty whitelist stays empty");
  const status = (await server.json("/api/memory-protection")).value;
  assert.equal(status.minimumProcessGrowthBytes, 536870912); assert.deepEqual(status.protectedProcesses, []);
  for (const [changes, reason] of [[{ minimumProcessGrowthBytes: 0 }, "invalid-minimum-process-growth-bytes"],
    [{ minimumProcessGrowthBytes: -1 }, "invalid-minimum-process-growth-bytes"],
    [{ protectedProcesses: [""] }, "invalid-protected-processes"], [{ protectedProcesses: [" explorer.exe"] }, "invalid-protected-processes"],
    [{ protectedProcesses: ["a.exe", "A.EXE"] }, "invalid-protected-processes"], [{ protectedProcesses: ["dir\\a.exe"] }, "invalid-protected-processes"],
    [{ protectedProcesses: ["C:\\Tools\\*.exe"] }, "invalid-protected-processes"]]) {
    const result = await patch(changes);
    assert.equal(result.status, 409, JSON.stringify(changes)); assert.equal(result.value.runtimeFailureCode, reason);
  }
  assert.equal((await patch({ protectedProcesses: ["explorer.exe", "C:\\Tools\\keep.exe"] })).status, 200);
  for (const [changes, path] of [[{ protectedProcesses: "explorer.exe" }, "$.memoryProtection.protectedProcesses"],
    [{ protectedProcesses: ["a.exe", 1] }, "$.memoryProtection.protectedProcesses[1]"],
    [{ minimumProcessGrowthBytes: 1.5 }, "$.memoryProtection.minimumProcessGrowthBytes"],
    [{ minimumProcessGrowthBytes: "1024" }, "$.memoryProtection.minimumProcessGrowthBytes"]]) {
    typeRejection(await patch(changes), path);
  }
});
test("a wrong JSON value type in any settings section is one owner rejection with its path", async () => {
  const initial = (await server.json("/api/settings/app")).value;
  const result = await server.json("/api/settings/app", body("PATCH", { expectedRevision: initial.revision,
    changes: { debug: { debugModeEnabled: "yes" } } }));
  typeRejection(result, "$.debug.debugModeEnabled");
  assert.equal(result.value.revision, initial.revision);
  assert.deepEqual((await server.json("/api/settings/app")).value.settings, initial.settings);
});
