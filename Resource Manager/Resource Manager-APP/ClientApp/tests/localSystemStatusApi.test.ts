import assert from "node:assert/strict";
import {
  buildLocalSystemStatusSubscriptionUrl,
  getLocalSystemStatus
} from "../src/data/localSystem/localSystemStatusApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:local-system-test"
});
const client = new RequestClient(owner);

assert.equal(
  buildLocalSystemStatusSubscriptionUrl(1_000.2),
  "/api/local-system/status/subscribe?intervalMs=1001");
assert.equal(
  buildLocalSystemStatusSubscriptionUrl(0),
  "/api/local-system/status/subscribe?intervalMs=1");

try {
  {
    let observedUrl = "";
    let observedInit: RequestInit | undefined;
    globalThis.fetch = (async (input, init) => {
      observedUrl = String(input);
      observedInit = init;
      return new Response(JSON.stringify({
        capturedAt: "2026-08-22T15:20:30.000Z",
        bootedAt: "2026-08-20T15:20:30.000Z",
        uptimeSeconds: 172_800
      }), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      });
    }) as typeof fetch;

    const value = await getLocalSystemStatus(client);
    assert.equal(observedUrl, "/api/local-system/status");
    assert.equal(observedInit?.method, "GET");
    assert.equal(observedInit?.cache, "no-store");
    assert.equal(value.uptimeSeconds, 172_800);
  }

  {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      capturedAt: "not-a-date",
      bootedAt: "2026-08-20T15:20:30.000Z",
      uptimeSeconds: 1
    }), { status: 200 })) as typeof fetch;

    await assert.rejects(
      getLocalSystemStatus(client),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "invalid-response"
        && error.attempt.decoderId === "local-system.status.v1");
  }

  {
    globalThis.fetch = (async () => new Response("temporarily unavailable", {
      status: 503
    })) as typeof fetch;

    await assert.rejects(
      getLocalSystemStatus(client),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "http"
        && error.status === 503
        && error.retryable);
  }
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
