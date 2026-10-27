import assert from "node:assert/strict";
import { getMemoryProtectionStatus, getNormalRunningPoint, createNormalRunningPoint,
  rebuildMemoryProtectionReserve, patchMemoryProtectionSettings } from "../src/data/memoryProtection/memoryProtectionApi.ts";
import { getGpuPlacementSoftwareSettings, getDefaultGpuPlacementSoftwarePolicy, saveGpuPlacementSoftwarePolicy } from "../src/api.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { memoryPoint, memoryStatus, memorySettingsResult, softwarePolicy } from "./fixtures/memoryProtection.ts";

const originalFetch = globalThis.fetch;
const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const client = new RequestClient(session);
const calls: Array<{ url: string; init?: RequestInit }> = [];
let payload: unknown; let status = 200;
globalThis.fetch = (async (input, init) => {
  calls.push({ url: String(input), init });
  return new Response(JSON.stringify(payload), { status, headers: { "Content-Type": "application/json" } });
}) as typeof fetch;
try {
  payload = memoryStatus;
  assert.deepEqual(await getMemoryProtectionStatus(client), memoryStatus);
  payload = null; assert.equal(await getNormalRunningPoint(client), null);
  payload = memoryPoint;
  assert.deepEqual(await getNormalRunningPoint(client), memoryPoint);
  assert.deepEqual(await createNormalRunningPoint(client), memoryPoint);
  assert.deepEqual(calls.map(call => [call.url, call.init?.method]), [
    ["/api/memory-protection", "GET"], ["/api/memory-protection/normal-running-point", "GET"],
    ["/api/memory-protection/normal-running-point", "GET"], ["/api/memory-protection/normal-running-point", "POST"]]);
  assert.equal(calls.at(-1)?.init?.body, undefined, "point creation is bodyless");
  for (const code of [200, 409]) {
    payload = memoryStatus; status = code;
    assert.deepEqual(await rebuildMemoryProtectionReserve(client), { status: code, value: memoryStatus });
    assert.equal(calls.at(-1)?.url, "/api/memory-protection/reserve/rebuild");
    assert.equal(calls.at(-1)?.init?.body, undefined);
  }
  for (const disposition of ["committedAndApplied", "committedWithCapabilityConstraints", "committedWithDeliveryFailures", "savedNotApplied"]) {
    status = ["savedNotApplied", "committedWithDeliveryFailures"].includes(disposition) ? 202 : 200;
    payload = { ...memorySettingsResult, runtimeApplicationDisposition: disposition };
    const saved = await patchMemoryProtectionSettings(client, "revision-a", { enabled: true });
    assert.equal(saved.accepted, true); assert.equal(saved.value.runtimeApplicationDisposition, disposition);
    assert.equal(calls.at(-1)?.url, "/api/memory-protection");
    assert.equal(calls.at(-1)?.init?.method, "PATCH");
    assert.deepEqual(JSON.parse(String(calls.at(-1)?.init?.body)), { expectedRevision: "revision-a", changes: { memoryProtection: { enabled: true } } });
  }
  for (const reason of ["invalid-action-mode", "no-normal-running-point", "disk-reserve-not-built", "reserve-not-configured",
    "insufficient-disk-space-for-reserve", "reserve-size-must-be-whole-clusters:cluster-bytes=65536", "invalid-reserve-location:Access denied"] ) {
    status = 409; payload = { ...memorySettingsResult, runtimeApplicationDisposition: "rejectedBeforeCommit", runtimeFailureCode: reason };
    const rejected = await patchMemoryProtectionSettings(client, "revision-a", {});
    assert.equal(rejected.accepted, false); assert.equal(rejected.value.runtimeFailureCode, reason);
    assert.equal(rejected.value.revision, "revision-a");
  }
  payload = { ...memorySettingsResult, runtimeApplicationDisposition: "revisionConflict" };
  assert.equal((await patchMemoryProtectionSettings(client, "stale", {})).value.runtimeApplicationDisposition, "revisionConflict");
  payload = {}; status = 409;
  await assert.rejects(rebuildMemoryProtectionReserve(client), ResponseDecodeError);
  await assert.rejects(patchMemoryProtectionSettings(client, "revision-a", {}), ResponseDecodeError);
  status = 503;
  await assert.rejects(getMemoryProtectionStatus(client), (error: unknown) => error instanceof RequestProblem && error.status === 503);
  await assert.rejects(createNormalRunningPoint(client), (error: unknown) => error instanceof RequestProblem && error.status === 503);
  status = 200; payload = {};
  await assert.rejects(getMemoryProtectionStatus(client), (error: unknown) => error instanceof RequestProblem && error.kind === "invalid-response");
  await assert.rejects(getNormalRunningPoint(client), (error: unknown) => error instanceof RequestProblem && error.kind === "invalid-response");
  payload = { softwarePolicy };
  const loaded = await getGpuPlacementSoftwareSettings("example-software", "Example");
  assert.equal(loaded.softwarePolicy.hostMemoryProtectionNoDump, true);
  payload = { ...softwarePolicy, hostMemoryProtectionNoDump: false };
  assert.equal((await getDefaultGpuPlacementSoftwarePolicy("example-software", "Example")).hostMemoryProtectionNoDump, false);
  const edited = { ...loaded.softwarePolicy, cpuSchedulingExcluded: true };
  for (const [code, disposition] of [[200, "committedAndPublished"], [202, "committedWithDeliveryFailures"], [409, "savedNotApplied"]] as const) {
    status = code; payload = { policy: edited, startupInterception: null, runtimeApplicationDisposition: disposition,
      startupInterceptionDisposition: "notAttempted", runtimePlanVersion: null, runtimePublicationSequence: null,
      runtimeDeliveryFailureCount: 0, failureCode: null };
    const saved = await saveGpuPlacementSoftwarePolicy(edited);
    assert.equal(saved.policy.hostMemoryProtectionNoDump, true);
    assert.equal(saved.runtimeApplicationDisposition, disposition);
    assert.deepEqual(JSON.parse(String(calls.at(-1)?.init?.body)), edited, "unrelated edit PUT preserves entire policy and flag");
  }
  status = 200; payload = { ...payload as object, runtimeApplicationDisposition: "committedAndApplied" };
  await assert.rejects(saveGpuPlacementSoftwarePolicy(edited), ResponseDecodeError);
} finally { globalThis.fetch = originalFetch; session.dispose(); }
console.log("memory protection and full software policy APIs: passed");
