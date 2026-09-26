import assert from "node:assert/strict";
import {
  getResourceMonitorState,
  normalizeResourceMonitorQuery
} from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { readyResourceMonitorWire } from "./resourceMonitorFixture.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:resource-monitor-test"
});
const client = new RequestClient(owner);

try {
  let observedUrl = "";
  globalThis.fetch = (async (input) => {
    observedUrl = String(input);
    return new Response(JSON.stringify(readyResourceMonitorWire()), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    });
  }) as typeof fetch;

  const query = normalizeResourceMonitorQuery({
    bars: [
      { metricId: "memory.usage", scaleMode: "capacity" },
      { metricId: "cpu.usage", scaleMode: "active" }
    ],
    sampleMetricIds: null,
    visibleColumnIds: ["name", "cpu"],
    sortColumnId: "impact",
    sortDirection: "desc",
    processDetailSoftwareIds: [],
    tableMode: "software"
  });
  const value = await getResourceMonitorState(client, query);
  const url = new URL(observedUrl, "http://localhost");
  assert.equal(url.pathname, "/api/resource-monitor/snapshot");
  assert.deepEqual(url.searchParams.getAll("ids"), ["cpu.usage", "memory.usage"]);
  assert.deepEqual(url.searchParams.getAll("columns"), ["name", "cpu"]);
  assert.equal(url.searchParams.get("scope"), "combined");
  assert.equal(url.searchParams.get("includeProcesses"), "all");
  assert.deepEqual(url.searchParams.getAll("expanded"), []);
  assert.equal(value.capturedAt, "2026-08-22T15:20:30.000Z");

  const invalid = readyResourceMonitorWire();
  invalid.version = 1 as never;
  globalThis.fetch = (async () => new Response(JSON.stringify(invalid), {
    status: 200
  })) as typeof fetch;
  await assert.rejects(
    getResourceMonitorState(client, query),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.decoderId === "resource-monitor.snapshot.v3");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}
