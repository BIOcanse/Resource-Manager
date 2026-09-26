import assert from "node:assert/strict";
import {
  buildOperationsSubscriptionUrl,
  getOperationsState
} from "../src/data/operations/operationsApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const validEnvelope = {
  schema: "host-manager.operations.state.v1",
  capturedAt: "2026-08-22T15:20:30.000Z",
  publicationRevision: "1",
  configurationGeneration: "7",
  ready: true,
  persistenceFaulted: false,
  faultStage: null,
  faultMessage: null,
  operations: []
};

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:operations-test"
});
const client = new RequestClient(owner);

try {
  assert.equal(buildOperationsSubscriptionUrl(), "/api/operations/subscribe");

  let observedUrl = "";
  let observedInit: RequestInit | undefined;
  globalThis.fetch = (async (input, init) => {
    observedUrl = String(input);
    observedInit = init;
    return new Response(JSON.stringify(validEnvelope), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    });
  }) as typeof fetch;

  const value = await getOperationsState(client);
  assert.equal(observedUrl, "/api/operations");
  assert.equal(observedInit?.method, "GET");
  assert.equal(observedInit?.cache, "no-store");
  assert.equal(value.publicationRevision, "1");

  globalThis.fetch = (async () => new Response(JSON.stringify([]), {
    status: 200
  })) as typeof fetch;
  await assert.rejects(
    getOperationsState(client),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.decoderId === "host-manager.operations.state.v1");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
