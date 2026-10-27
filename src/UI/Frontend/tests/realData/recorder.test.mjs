import assert from "node:assert/strict";
import { test } from "node:test";
import {
  assertAllowedRequest,
  deriveProductionReadPlan
} from "./productionReadPlan.mjs";
import { recordBackendSlice } from "./recordBackendSlice.mjs";
import {
  assertRawSlicePath,
  matchSelector,
  rawSliceRoot,
  readSlice,
  resolveSliceFilePath,
  selectorKey,
  validateSlice
} from "./sliceFormat.mjs";
import { resolve } from "node:path";

test("production plan contains reachable read descriptors and catalog-backed selectors", async () => {
  const initial = await deriveProductionReadPlan();
  assert.ok(initial.reads.includes("/api/metrics/catalog"));
  assert.ok(initial.reads.includes("/api/resource-breakdown/fields/catalog"));
  assert.ok(initial.reads.includes("/api/software"));
  assert.ok(initial.reads.includes("/api/browser-runtimes"));
  assert.ok(initial.reads.includes("/api/optimization/protection"));
  assert.ok(initial.reads.includes("/api/updates/product/versions"));
  assert.ok(initial.reads.includes("/api/targeted-recordings"), "a RequestClient descriptor with no request options defaults to GET");
  assert.ok(initial.reads.includes("/api/settings/app"));
  assert.ok(initial.reads.includes("/api/settings/report-rules"));
  assert.ok(initial.reads.includes("/api/disk-usage/volumes"));
  assert.ok(initial.reads.includes("/api/disk-usage/summary"));
  assert.ok(!initial.reads.includes("/api/dashboard/settings"), "dashboard settings are read from frontend preferences, not a backend route");
  assert.ok(!initial.reads.some(path => /refresh=true/i.test(path)));
  assert.ok(initial.discoveryPath.startsWith("/api/metrics/fields/catalog/subscribe?"));
  assert.ok(initial.reads.every(path => path.startsWith("/api/")));
  assert.ok(initial.subscriptions.every(path => new URL(path, "http://localhost").pathname.endsWith("/subscribe")));
  assert.ok(![...initial.reads, ...initial.subscriptions].some(path => /credentials?|access-keys?|access-requests?|draft|refresh|reapply/i.test(path)));

  const expanded = await deriveProductionReadPlan(750, {
    metrics: [
      { id: "cpu.usage" }, { id: "memory.usage" }, { id: "virtualMemory.usage" },
      { id: "disk.io" }, { id: "network.traffic" }, { id: "gpu.0.usage" },
      { id: "gpu.0.vram" }, { id: "cpu.temperature" }
    ],
    processFields: [
      { name: "process.memory.working-set", supportsResourceFacts: true, resourceFactTotalMetricId: "memory.usage" },
      { name: "process.temperature", supportsResourceFacts: true, resourceFactTotalMetricId: "cpu.temperature" }
    ],
    metricFields: [{ fields: [{ name: "cpu.core.usage" }, { name: "gpu.engine.usage" }] }]
  });
  const facts = expanded.subscriptions.find(path => new URL(path, "http://localhost").pathname === "/api/resource-facts/subscribe");
  const metrics = expanded.subscriptions.find(path => {
    const url = new URL(path, "http://localhost");
    return url.pathname === "/api/metrics/subscribe" && url.searchParams.has("ids");
  });
  const fields = expanded.subscriptions.find(path => {
    const url = new URL(path, "http://localhost");
    return url.pathname === "/api/metrics/subscribe" && url.searchParams.has("fields");
  });
  const gpu = expanded.subscriptions.find(path => new URL(path, "http://localhost").pathname === "/api/metrics/gpu-specialized/subscribe");
  assert.ok(facts);
  assert.ok(new URL(facts, "http://localhost").searchParams.getAll("items").includes("process.inventory"));
  assert.ok(new URL(facts, "http://localhost").searchParams.getAll("items").includes("process.attribution"));
  assert.ok(new URL(facts, "http://localhost").searchParams.getAll("items").includes("process.memory.working-set"));
  assert.ok(!new URL(facts, "http://localhost").searchParams.getAll("items").includes("cpu.temperature"));
  assert.ok(metrics && new URL(metrics, "http://localhost").searchParams.getAll("ids").includes("gpu.0.usage"));
  assert.ok(fields && new URL(fields, "http://localhost").searchParams.get("fields")?.includes("gpu.engine.usage"));
  assert.ok(gpu && new URL(gpu, "http://localhost").searchParams.getAll("ids").some(id => id.startsWith("gpu.0.")));
  const bars = expanded.subscriptions.find(path => new URL(path, "http://localhost").pathname === "/api/resource-monitor/subscribe");
  assert.ok(bars);
  assert.ok(!new URL(bars, "http://localhost").searchParams.getAll("ids").includes("cpu.temperature"));
});

test("request gate allows only listed GETs and the exact unified subscription body", async () => {
  const plan = await deriveProductionReadPlan();
  assert.doesNotThrow(() => assertAllowedRequest(plan, "GET", plan.reads[0], undefined));
  assert.throws(() => assertAllowedRequest(plan, "GET", plan.reads[0], {}), /refused/);
  assert.throws(() => assertAllowedRequest(plan, "GET", "/api/ai-gateway/credentials", undefined), /refused/);
  assert.throws(() => assertAllowedRequest(plan, "POST", "/api/settings/app", {}), /refused/);
  const body = {
    version: 1,
    subscriptions: plan.subscriptions.map((path, index) => ({ id: `slice:${index}`, path }))
  };
  assert.doesNotThrow(() => assertAllowedRequest(plan, "POST", plan.streamPath, body));
  assert.throws(() => assertAllowedRequest(plan, "POST", plan.streamPath, { ...body, version: 2 }), /refused/);
});

test("selector matching ignores interval and only permits explicit ids/items subsets", () => {
  const recordedIds = "/api/metrics/subscribe?ids=cpu.usage&ids=gpu.0.usage&intervalMs=1000";
  assert.equal(selectorKey(recordedIds), selectorKey("/api/metrics/subscribe?intervalMs=300&ids=gpu.0.usage&ids=cpu.usage"));
  assert.deepEqual(matchSelector(recordedIds, "/api/metrics/subscribe?ids=cpu.usage&intervalMs=500"), {
    kind: "subset", parameter: "ids", values: ["cpu.usage"]
  });
  assert.deepEqual(matchSelector("/api/resource-facts/subscribe?items=cpu.usage&items=memory.usage&intervalMs=1000",
    "/api/resource-facts/subscribe?intervalMs=250&items=memory.usage"), {
    kind: "subset", parameter: "items", values: ["memory.usage"]
  });
  assert.deepEqual(matchSelector(recordedIds, "/api/metrics/subscribe?ids=cpu.usage&ids=gpu.0.usage&intervalMs=400"), { kind: "exact" });
  assert.equal(matchSelector(recordedIds, "/api/metrics/gpu-specialized/subscribe?ids=cpu.usage"), null);
  assert.equal(matchSelector(recordedIds, "/api/metrics/subscribe?ids=cpu.usage.extra"), null);
  assert.equal(matchSelector(recordedIds, "/api/metrics/subscribe?ids=&intervalMs=1000"), null);
  assert.equal(matchSelector("/api/metrics/subscribe?intervalMs=1000", "/api/metrics/subscribe?ids=cpu.usage"), null);
  assert.equal(matchSelector("/api/metrics/subscribe?ids=cpu.usage&scope=table",
    "/api/metrics/subscribe?ids=cpu.usage&scope=bars"), null);
  const groupedFields = "/api/metrics/subscribe?device=A&fields=cpu.usage&device=B&fields=gpu.usage&intervalMs=1000";
  const swappedFields = "/api/metrics/subscribe?device=A&fields=gpu.usage&device=B&fields=cpu.usage&intervalMs=500";
  assert.notEqual(selectorKey(groupedFields), selectorKey(swappedFields));
  assert.equal(matchSelector(groupedFields, swappedFields), null);
});

test("slice paths accept a raw slice directory or file and reject paths outside the raw root", async () => {
  const stamp = "20261026T220000Z";
  const directory = resolve(rawSliceRoot, stamp);
  const file = resolve(directory, "slice.json");
  assert.equal(resolveSliceFilePath(directory), file);
  assert.equal(resolveSliceFilePath(file), file);
  assert.doesNotThrow(() => assertRawSlicePath(file));
  assert.throws(() => assertRawSlicePath(resolve(directory, "other.json")), /real-slices/);
  await assert.rejects(readSlice(resolve("outside-slice.json")), /real-slices/);
});

test("slice validation retains timestamped raw response and frame events", () => {
  const slice = {
    schemaVersion: 1,
    startedAt: "2026-10-26T22:00:00.000Z",
    durationMs: 1000,
    complete: true,
    events: [
      { atMs: 2, kind: "response", path: "/api/metrics/catalog", status: 200, contentType: "application/json", encoding: "json", body: [{ id: "cpu.usage" }] },
      { atMs: 20, kind: "frame", path: "/api/metrics/subscribe?ids=cpu.usage&intervalMs=1000", body: { items: { "cpu.usage": 1 } } }
    ]
  };
  assert.equal(validateSlice(slice), slice);
  assert.throws(() => validateSlice({ ...slice, startedAt: "yesterday" }), /header/);
  assert.throws(() => validateSlice({ ...slice, events: [{ ...slice.events[0], path: "/api/ai-gateway/credentials" }] }), /event/);
  assert.throws(() => validateSlice({ ...slice, events: [{ ...slice.events[0], encoding: undefined }] }), /response/);
});

test("recorder rejects a non-loopback URL and missing authorization before fetching or writing", async () => {
  await assert.rejects(recordBackendSlice({
    baseUrl: "https://example.com/",
    durationMs: 1000,
    output: "outside-slice.json",
    keyless: true,
    fetchImpl: async () => assert.fail("fetch must not run")
  }), /loopback/);
  await assert.rejects(recordBackendSlice({
    baseUrl: "http://127.0.0.1:10421/",
    durationMs: 1000,
    output: "outside-slice.json",
    fetchImpl: async () => assert.fail("fetch must not run")
  }), /authorized key/);
});
