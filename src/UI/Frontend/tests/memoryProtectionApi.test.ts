import assert from "node:assert/strict";
import { buildMemoryProtectionSubscriptionUrl, createNormalRunningPoint, deleteNormalRunningPoint, listNormalRunningPoints,
  rebuildMemoryProtectionReserve, selectNormalRunningPoint } from "../src/data/memoryProtection/memoryProtectionApi.ts";
import { getGpuPlacementSoftwareSettings, getDefaultGpuPlacementSoftwarePolicy, saveGpuPlacementSoftwarePolicy } from "../src/api.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { memoryPoint, memoryPointList, memoryStatus, softwarePolicy } from "./fixtures/memoryProtection.ts";

const originalFetch = globalThis.fetch;
const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const client = new RequestClient(session);
const calls: Array<{ url: string; init?: RequestInit }> = [];
let payload: unknown; let status = 200;
globalThis.fetch = (async (input, init) => {
  calls.push({ url: String(input), init });
  if (status === 204) return new Response(null, { status });
  return new Response(JSON.stringify(payload), { status, headers: { "Content-Type": "application/json" } });
}) as typeof fetch;
try {
  // Status, records and the point have no GET consumer in the frontend: they are read from the subscription only.
  assert.equal(buildMemoryProtectionSubscriptionUrl(), "/api/memory-protection/subscribe");
  payload = memoryPoint;
  assert.deepEqual(await createNormalRunningPoint(client), { status: "done", value: memoryPoint });
  assert.deepEqual(calls.map(call => [call.url, call.init?.method]), [["/api/memory-protection/normal-running-point", "POST"]]);
  assert.equal(calls.at(-1)?.init?.body, undefined, "point creation is bodyless");
  // Saved points: the refusals the backend names come back as their reason code; any other failure still throws.
  payload = { error: "normal-running-point-limit" }; status = 409;
  assert.deepEqual(await createNormalRunningPoint(client), { status: "refused", reason: "normal-running-point-limit" });
  payload = memoryPointList; status = 200;
  assert.deepEqual(await listNormalRunningPoints(client), memoryPointList);
  assert.deepEqual([calls.at(-1)?.url, calls.at(-1)?.init?.method ?? "GET"], ["/api/memory-protection/normal-running-points", "GET"]);
  payload = memoryPoint;
  assert.deepEqual(await selectNormalRunningPoint(client, memoryPoint.id), { status: "done", value: memoryPoint });
  assert.deepEqual([calls.at(-1)?.url, calls.at(-1)?.init?.method, JSON.parse(String(calls.at(-1)?.init?.body))],
    ["/api/memory-protection/normal-running-points/active", "PUT", { id: memoryPoint.id }]);
  payload = { error: "normal-running-point-not-found" }; status = 404;
  assert.deepEqual(await selectNormalRunningPoint(client, "missing"), { status: "refused", reason: "normal-running-point-not-found" });
  assert.deepEqual(await deleteNormalRunningPoint(client, "a/b"), { status: "refused", reason: "normal-running-point-not-found" });
  assert.deepEqual([calls.at(-1)?.url, calls.at(-1)?.init?.method], ["/api/memory-protection/normal-running-points/a%2Fb", "DELETE"]);
  status = 204;
  assert.deepEqual(await deleteNormalRunningPoint(client, memoryPoint.id), { status: "done", value: null });
  payload = { error: "normal-running-point-limit" }; status = 404;
  assert.deepEqual(await deleteNormalRunningPoint(client, "x"), { status: "refused", reason: "normal-running-point-limit" },
    "the reason is passed on as sent");
  payload = { error: "normal-running-point-not-found" }; status = 409;
  await assert.rejects(deleteNormalRunningPoint(client, "x"), (error: unknown) => error instanceof RequestProblem && error.status === 409,
    "only the status the endpoint refuses with is a refusal");
  payload = "text"; status = 404;
  await assert.rejects(selectNormalRunningPoint(client, "x"), (error: unknown) => error instanceof RequestProblem && error.status === 404);
  for (const code of [200, 409]) {
    payload = memoryStatus; status = code;
    assert.deepEqual(await rebuildMemoryProtectionReserve(client), { status: code, value: memoryStatus });
    assert.equal(calls.at(-1)?.url, "/api/memory-protection/reserve/rebuild");
    assert.equal(calls.at(-1)?.init?.body, undefined);
  }
  payload = {}; status = 409;
  await assert.rejects(rebuildMemoryProtectionReserve(client), ResponseDecodeError);
  status = 503;
  await assert.rejects(createNormalRunningPoint(client), (error: unknown) => error instanceof RequestProblem && error.status === 503);
  status = 200; payload = {};
  await assert.rejects(createNormalRunningPoint(client), (error: unknown) => error instanceof RequestProblem && error.kind === "invalid-response");
  payload = { softwarePolicy };
  const loaded = await getGpuPlacementSoftwareSettings("example-software", "Example");
  assert.equal(loaded.softwarePolicy.hostMemoryProtectionNoDump, true);
  assert.equal(loaded.softwarePolicy.hostMemoryProtectionNeverTerminate, true);
  payload = { ...softwarePolicy, hostMemoryProtectionNoDump: false };
  assert.equal((await getDefaultGpuPlacementSoftwarePolicy("example-software", "Example")).hostMemoryProtectionNoDump, false);
  const edited = { ...loaded.softwarePolicy, cpuSchedulingExcluded: true };
  for (const [code, disposition] of [[200, "committedAndPublished"], [202, "committedWithDeliveryFailures"], [409, "savedNotApplied"]] as const) {
    status = code; payload = { policy: edited, startupInterception: null, runtimeApplicationDisposition: disposition,
      startupInterceptionDisposition: "notAttempted", runtimePlanVersion: null, runtimePublicationSequence: null,
      runtimeDeliveryFailureCount: 0, failureCode: null };
    const saved = await saveGpuPlacementSoftwarePolicy(edited);
    assert.equal(saved.policy.hostMemoryProtectionNoDump, true);
    assert.equal(saved.policy.hostMemoryProtectionNeverTerminate, true);
    assert.equal(saved.runtimeApplicationDisposition, disposition);
    assert.deepEqual(JSON.parse(String(calls.at(-1)?.init?.body)), edited, "unrelated edit PUT preserves entire policy and flag");
  }
  status = 200; payload = { ...payload as object, runtimeApplicationDisposition: "committedAndApplied" };
  await assert.rejects(saveGpuPlacementSoftwarePolicy(edited), ResponseDecodeError);
} finally { globalThis.fetch = originalFetch; session.dispose(); }
console.log("memory protection and full software policy APIs: passed");
