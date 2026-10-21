import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { getDeviceTopologyState } from "../src/features/deviceTopology/deviceTopologyApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:device-topology-test"
});
const client = new RequestClient(owner);

try {
  let observedUrl = "";
  globalThis.fetch = (async (input) => {
    observedUrl = String(input);
    return new Response(JSON.stringify({
      schemaVersion: "4.0.0",
      state: "warming",
      snapshot: null,
      contentGeneration: 0,
      stateRevision: 1,
      source: "memory",
      lastSuccessAt: null,
      lastAttemptAt: null,
      failureCode: null,
      attemptDiagnostics: []
    }), { status: 200 });
  }) as typeof fetch;

  const value = await getDeviceTopologyState(client);
  assert.equal(observedUrl, "/api/device-topology/state");
  assert.equal(value.stateRevision, 1);

  const edidWire = JSON.parse(readFileSync(new URL("./fixtures/displayEdid.json", import.meta.url), "utf8"));
  globalThis.fetch = (async () => new Response(JSON.stringify(edidWire), { status: 200 })) as typeof fetch;
  const edid = (await getDeviceTopologyState(client)).snapshot!.ports[0].display!;
  assert.equal(edid.currentRefreshRateDenominator, 1001); assert.equal(edid.modeCapabilityDeclared!.modes.length, 7);
  assert.equal(edid.edidRawDeclaredBase64, edidWire.snapshot.ports[0].display.edidRawDeclaredBase64);
  globalThis.fetch = (async () => new Response(JSON.stringify({
    schemaVersion: "1.0.0",
    state: "warming"
  }), { status: 200 })) as typeof fetch;
  await assert.rejects(
    getDeviceTopologyState(client),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.decoderId === "device-topology.state.v3");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
