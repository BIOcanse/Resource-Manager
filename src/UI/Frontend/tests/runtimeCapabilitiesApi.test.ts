import assert from "node:assert/strict";
import { getRuntimeCapabilities } from "../src/data/runtimeCapabilities/runtimeCapabilitiesApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:runtime-capabilities-test"
});
const client = new RequestClient(owner);

try {
  let observedUrl = "";
  let observedInit: RequestInit | undefined;
  globalThis.fetch = (async (input, init) => {
    observedUrl = String(input);
    observedInit = init;
    return new Response(JSON.stringify({
      profileId: "test",
      readOnly: false,
      mutablePersistence: true,
      legacyPersistenceImport: false,
      gpuLaunchInterceptionReconciliation: false,
      runtimeEffectOwners: false,
      publicServiceCoordination: false,
      optimizationRuntime: false,
      sharedResourceOwnership: false
    }), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    });
  }) as typeof fetch;

  const value = await getRuntimeCapabilities(client);
  assert.equal(observedUrl, "/api/runtime/capabilities");
  assert.equal(observedInit?.method, "GET");
  assert.equal(observedInit?.cache, "no-store");
  assert.equal(value.profileId, "test");

  globalThis.fetch = (async () => new Response(JSON.stringify({
    profileId: "test",
    readOnly: "no"
  }), { status: 200 })) as typeof fetch;
  await assert.rejects(
    getRuntimeCapabilities(client),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.decoderId === "runtime.capabilities.v1");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
