import assert from "node:assert/strict";
import {
  getOptimizationReports,
  refreshOptimizationReports
} from "../src/data/optimization/optimizationReportsApi.ts";
import { RequestClient } from
  "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from
  "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from
  "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:optimization-reports-test"
});
const client = new RequestClient(owner);

try {
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  globalThis.fetch = (async (input, init) => {
    requests.push({ url: String(input), init });
    return jsonResponse(optimizationOverview());
  }) as typeof fetch;

  assert.equal((await getOptimizationReports(client)).status.configuredRuleCount, 7);
  assert.equal((await refreshOptimizationReports(client)).status.availableRuleCount, 6);
  assert.deepEqual(requests.map((request) => request.url), [
    "/api/optimization/reports",
    "/api/optimization/reports/refresh"
  ]);
  assert.deepEqual(requests.map((request) => request.init?.method), ["GET", "POST"]);

  globalThis.fetch = (async () => jsonResponse({
    ...optimizationOverview(),
    status: { recorderRunning: true }
  })) as typeof fetch;
  await assert.rejects(
    getOptimizationReports(client),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.decoderId === "optimization.reports.overview.v2");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}

function optimizationOverview() {
  return {
    capturedAt: "2026-08-26T08:00:00.000Z",
    reports: [],
    trustedTargets: [],
    protectedTargets: [],
    status: {
      lastEvaluationAt: "2026-08-26T08:00:00.000Z",
      lastObservedAt: "2026-08-26T07:59:58.000Z",
      configuredRuleCount: 7,
      availableRuleCount: 6,
      activeReportCount: 0,
      trustedCount: 0,
      protectedCount: 0,
      sampleIntervalSeconds: 10
    }
  };
}

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" }
  });
}
