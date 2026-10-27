import assert from "node:assert/strict";
import type { GpuPlacementProcessPolicy, GpuPlacementSoftwarePolicy } from "../src/types.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { ApiRequestError, requestJson } from "../src/api/httpTransport.ts";
import { isolatedSourceFunctions } from "./helpers/isolatedSourceFunctions.ts";
import { decodeRuntimePolicySaveResult } from "../src/api/runtimePolicySaveResultDecoder.ts";
import { decodeSoftwarePolicy } from "../src/data/softwarePolicy/softwarePolicyDecoder.ts";
import { softwarePolicy } from "./fixtures/memoryProtection.ts";
import { cpuTopologyDecoder, cpuResidencyDecoder, cpuExclusiveBindingsDecoder } from "../src/data/cpu/cpuSourceDecoders.ts";
import { uiText } from "../src/text.ts";

// api.ts is a Vite module with extensionless imports. Reuse the existing source
// isolation helper to run its actual functions with production HTTP and decoders.
const {
  saveCpuCorePerformanceOverrides, resetCpuCorePerformanceOverrides,
  saveGpuPerformanceScoreOverrides, resetGpuPerformanceScoreOverrides,
  saveGpuPlacementProcessPolicy, saveGpuPlacementSoftwarePolicy,
  getCpuTopology, getCpuResidency, getCpuExclusiveBindings
} = isolatedSourceFunctions(new URL("../src/api.ts", import.meta.url), [
  "getJson", "getCpuTopology", "getCpuResidency", "getCpuExclusiveBindings",
  "saveCpuCorePerformanceOverrides", "resetCpuCorePerformanceOverrides",
  "saveGpuPerformanceScoreOverrides", "resetGpuPerformanceScoreOverrides",
  "saveGpuPlacementProcessPolicy", "saveGpuPlacementSoftwarePolicy",
  "saveRuntimePolicy", "resetRuntimePolicy", "writeRuntimePolicy"
], { URLSearchParams, requestJson, ApiRequestError, decodeRuntimePolicySaveResult, decodeSoftwarePolicy,
  cpuTopologyDecoder, cpuResidencyDecoder, cpuExclusiveBindingsDecoder, uiText });

const originalFetch = globalThis.fetch;
const writes: Array<[string, string]> = [];
let status = 200;
let payload: unknown;
globalThis.fetch = async (url, options) => {
  writes.push([String(url), options?.method ?? "GET"]);
  return new Response(payload === undefined ? "" : JSON.stringify(payload), { status, headers: { "content-type": "application/json" } });
};
const envelope = {
  policy: softwarePolicy, startupInterception: null, runtimeApplicationDisposition: "committedAndPublished",
  startupInterceptionDisposition: "notAttempted", runtimePlanVersion: 3,
  runtimePublicationSequence: 7, runtimeDeliveryFailureCount: 0, failureCode: null
};
const consumers = [
  () => saveCpuCorePerformanceOverrides({ cpuName: "Fixture", scores: [] }),
  () => resetCpuCorePerformanceOverrides("Fixture"),
  () => saveGpuPerformanceScoreOverrides({ scores: [] }),
  () => resetGpuPerformanceScoreOverrides(),
  () => saveGpuPlacementSoftwarePolicy({ softwareId: "fixture" } as GpuPlacementSoftwarePolicy),
  () => saveGpuPlacementProcessPolicy({ processKey: "fixture" } as GpuPlacementProcessPolicy)
];
try {
  for (const [disposition, httpStatus] of [["committedAndPublished", 200], ["committedWithDeliveryFailures", 202], ["savedNotApplied", 409]] as const) {
    status = httpStatus;
    payload = { ...envelope, runtimeApplicationDisposition: disposition };
    for (const consume of consumers) assert.equal((await consume()).runtimeApplicationDisposition, disposition);
  }
  for (const httpStatus of [200, 409]) {
    status = httpStatus;
    payload = { ...envelope, runtimeApplicationDisposition: "committedAndApplied", startupInterceptionDisposition: "applyFailed" };
    for (const consume of consumers) await assert.rejects(consume, ResponseDecodeError);
  }
  status = 409;
  payload = { ...envelope, startupInterceptionDisposition: "applyFailed" };
  assert.equal((await saveGpuPlacementProcessPolicy({ processKey: "fixture" } as GpuPlacementProcessPolicy)).runtimeApplicationDisposition, "committedAndPublished");
  status = 200;
  for (const [consume, reasonCode] of [[getCpuTopology, "cpu-topology-not-published"], [getCpuExclusiveBindings, "cpu-topology-not-published"], [getCpuResidency, "cpu-residency-window-warming"]] as const) {
    payload = { state: "unavailable", reasonCode };
    assert.deepEqual(await consume(), payload);
    payload = null;
    await assert.rejects(consume, ResponseDecodeError);
    payload = undefined;
    await assert.rejects(consume, ApiRequestError);
  }
  assert.deepEqual(writes.slice(0, 6).map(([, method]) => method), ["PUT", "DELETE", "PUT", "DELETE", "PUT", "PUT"]);
} finally { globalThis.fetch = originalFetch; }
console.log("All runtime policy writers and CPU GET API consumers use strict contract decoding");
