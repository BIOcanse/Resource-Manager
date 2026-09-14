import assert from "node:assert/strict";
import {
  buildResourceManagerSelfSchedulingSubscriptionUrl,
  getResourceManagerSelfScheduling
} from "../src/data/selfScheduling/resourceManagerSelfSchedulingApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:self-scheduling-test"
});
const client = new RequestClient(owner);

assert.equal(
  buildResourceManagerSelfSchedulingSubscriptionUrl(5_000),
  "/api/adapters/resource-manager/scheduling/subscribe?intervalMs=5000");
assert.equal(
  buildResourceManagerSelfSchedulingSubscriptionUrl(-1),
  "/api/adapters/resource-manager/scheduling/subscribe?intervalMs=1");

const validResponse = {
  cpuGrade: "normal",
  gpuGrade: "optimize",
  cpuUpdatedAt: "2026-08-22T15:20:30.000Z",
  gpuUpdatedAt: "2026-08-22T15:20:31.000Z",
  cpuPolicyId: "default-policy",
  gpuPolicyId: "foreground-policy",
  cpuReason: "default state",
  gpuReason: "foreground workload",
  sources: []
};

try {
  {
    let observedUrl = "";
    let observedInit: RequestInit | undefined;
    globalThis.fetch = (async (input, init) => {
      observedUrl = String(input);
      observedInit = init;
      return new Response(JSON.stringify(validResponse), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      });
    }) as typeof fetch;

    const value = await getResourceManagerSelfScheduling(client);
    assert.equal(observedUrl, "/api/adapters/resource-manager/scheduling");
    assert.equal(observedInit?.method, "GET");
    assert.equal(observedInit?.cache, "no-store");
    assert.equal(value.gpuGrade, "optimize");
  }

  {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      ...validResponse,
      cpuGrade: "unknown"
    }), { status: 200 })) as typeof fetch;

    await assert.rejects(
      getResourceManagerSelfScheduling(client),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "invalid-response"
        && error.attempt.decoderId === "resource-manager.self-scheduling.v1");
  }

  {
    globalThis.fetch = (async () => new Response("conflict", {
      status: 409
    })) as typeof fetch;

    await assert.rejects(
      getResourceManagerSelfScheduling(client),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "http"
        && error.status === 409
        && !error.retryable);
  }

  {
    const abortController = new AbortController();
    abortController.abort("test cancellation");

    await assert.rejects(
      getResourceManagerSelfScheduling(client, abortController.signal),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "aborted");
  }
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
