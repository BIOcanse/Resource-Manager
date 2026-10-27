import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { memoryProtectionScenarios } from "../memoryProtectionFixture.mjs";
import { memoryProtectionStatusDecoder, nullableNormalRunningPointDecoder } from "../../../src/data/memoryProtection/memoryProtectionDecoders.ts";
import { appSettingsResultDecoder } from "../../../src/data/appSettings/appSettingsResultDecoder.ts";
import { softwarePolicyDecoder } from "../../../src/data/softwarePolicy/softwarePolicyDecoder.ts";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });
const body = (method, value) => ({ method, headers: { "content-type": "application/json" }, body: JSON.stringify(value) });
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
    assert.deepEqual(nullableNormalRunningPointDecoder.decode(point), point);
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
  assert.equal(built.status, 200); assert.equal(built.value.settings.version, "1.0.36");
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
  for (const changes of [{ memoryProtection: null }, { memoryProtection: { actionMode: null } },
    { memoryProtection: { reserves: null } }, { memoryProtection: { unknown: true } }]) {
    const rejected = await server.json("/api/memory-protection", body("PATCH", { expectedRevision: initial.revision, changes }));
    assert.equal(rejected.status, 400);
    assert.match(rejected.value.error, /cannot be removed|Unknown application settings field/);
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
  assert.equal(status.excludedCandidates.length, 8); assert.equal(status.actionRecordLimit, 64);
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
    const edited = { ...loaded, cpuSchedulingExcluded: !loaded.cpuSchedulingExcluded };
    const unrelated = await server.json(`${path}/policy`, body("PUT", edited));
    assert.equal(unrelated.value.policy.hostMemoryProtectionNoDump, true);
    assert.equal(unrelated.value.policy.cpuSchedulingExcluded, edited.cpuSchedulingExcluded);
    assert.equal((await server.json(path)).value.softwarePolicy.hostMemoryProtectionNoDump, true);
  }
});
