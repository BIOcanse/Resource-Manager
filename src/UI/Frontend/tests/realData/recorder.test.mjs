import assert from "node:assert/strict";
import { test } from "node:test";
import {
  assertAllowedRequest,
  deriveProductionReadPlan
} from "./productionReadPlan.mjs";
import { captureBackendSlice, recordBackendSlice } from "./recordBackendSlice.mjs";
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
  assert.ok(!new URL(facts, "http://localhost").searchParams.getAll("items").includes("process.memory.working-set"), "unselected source choices do not become demand");
  assert.ok(!new URL(facts, "http://localhost").searchParams.getAll("items").includes("cpu.temperature"));
  assert.ok(metrics && new URL(metrics, "http://localhost").searchParams.getAll("ids").includes("gpu.0.usage"));
  assert.ok(fields && !new URL(fields, "http://localhost").searchParams.get("fields")?.includes("gpu.engine.usage"), "only actual page field selections are requested");
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
  const previousRoot = process.env.RM_REAL_SLICE_ROOT;
  try {
    delete process.env.RM_REAL_SLICE_ROOT;
    assert.throws(() => rawSliceRoot(), /RM_REAL_SLICE_ROOT/);
    process.env.RM_REAL_SLICE_ROOT = resolve("example-workset", "real-slices");
    const stamp = "20261026T220000Z";
    const directory = resolve(rawSliceRoot(), stamp);
    const file = resolve(directory, "slice.json");
    assert.equal(resolveSliceFilePath(directory), file);
    assert.equal(resolveSliceFilePath(file), file);
    assert.doesNotThrow(() => assertRawSlicePath(file));
    assert.throws(() => assertRawSlicePath(resolve(directory, "other.json")), /real-slices/);
    await assert.rejects(readSlice(resolve("outside-slice.json")), /real-slices/);
  } finally {
    if (previousRoot === undefined) delete process.env.RM_REAL_SLICE_ROOT;
    else process.env.RM_REAL_SLICE_ROOT = previousRoot;
  }
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

// Raw machine data stays outside Git; queue-fe supplies the original failed slice.
// Ordinary contracts use a generated large catalog with the same rejection shape.
import { readFile, writeFile } from "node:fs/promises";
import { metricCatalogPushDecoder } from "../../src/data/monitor/metricCatalogPush.ts";
import { dashboardPointValues, dashboardMetricAvailable } from "../../src/preferences/dashboardPreferences.ts";
import { buildMetricSnapshotSubscriptionUrl } from "../../src/data/monitor/monitorSourcesApi.ts";
import { fieldCardSlot } from "../../src/features/monitor/fieldCards.ts";
import { normalizeResourceTableColumns } from "../../src/stores/monitorConfig.ts";
import { resourceTableMetricId } from "../../src/features/resourceTable/resourceTableSources.ts";
import { resourceBarQuery, resourceBarResidualDetailMetricIds } from "../../src/features/resourceBreakdown/resourceBarSources.ts";
import { buildResourceMonitorSubscriptionUrl, normalizeResourceMonitorQuery } from "../../src/data/resourceMonitor/resourceMonitorApi.ts";
import { buildMetricFieldSubscriptionUrl } from "../../src/data/metricFields/metricFieldSource.ts";
import { numaFieldNames, effectiveFrequencyFieldName } from "../../src/features/details/cpuMetricFields.ts";
import { deviceMetricFieldNames } from "../../src/features/deviceTopology/deviceMetricFields.ts";
import { buildGpuSpecializedCounterIds, buildGpuSpecializedTelemetrySubscriptionUrl } from "../../src/data/gpu/gpuSchedulingModelSource.ts";

async function regressionInput() {
  if (process.env.RM_RECORDER_REGRESSION_SLICE) return JSON.parse(await readFile(process.env.RM_RECORDER_REGRESSION_SLICE, "utf8"));
  const definition = id => ({ id, unit: "%", requiredComponentId: null, selectable: true, disabledReason: null,
    scopeKind: null, scopeKey: null, hasDedicatedMemoryMetrics: null, candidateSources: [] });
  const metrics = ["cpu.usage", "memory.usage", ...Array.from({ length: 1545 }, (_, index) => `${1000000000000001n + BigInt(index)}/disk_all_total_active_percent`)].map(definition);
  const fields = JSON.parse(await readFile(new URL("../fixtures/metricFieldsWire/catalog.json", import.meta.url), "utf8"));
  const oldPath = buildMetricSnapshotSubscriptionUrl({ ids: metrics.map(metric => metric.id) }, 1000);
  return { events: [
    { kind: "response", path: "/api/metrics/catalog", body: metrics, status: 200, contentType: "application/json" },
    { kind: "response", path: "/api/resource-breakdown/fields/catalog", body: [], status: 200, contentType: "application/json" },
    { kind: "frame", path: "/api/metrics/fields/catalog/subscribe?intervalMs=1000", body: { catalogRevision: "test", metrics, fields } },
    { kind: "frame", path: oldPath, body: { statusCode: 400, error: "unsupported-subscription-selector" } }
  ] };
}

function simulatedBackend(input, { refuse = false, httpRefuse = false, warming = false } = {}) {
  const catalog = input.events.find(event => event.kind === "frame" && event.path.startsWith("/api/metrics/fields/catalog/subscribe")).body;
  const refusal = input.events.find(event => event.body?.statusCode === 400).body;
  const readEvents = new Map(input.events.filter(event => event.kind === "response").map(event => [event.path, event]));
  const calls = [], endedAt = [];
  let captures = 0;
  return { calls, endedAt, fetchImpl: async (url, options) => {
    if (options.method === "GET") {
      const event = readEvents.get(url.pathname + url.search);
      return new Response(JSON.stringify(event?.body ?? []), { status: event?.status ?? 200, headers: { "content-type": "application/json" } });
    }
    const subscriptions = JSON.parse(options.body).subscriptions;
    const full = subscriptions.some(item => item.path.startsWith("/api/resource-facts/subscribe"));
    calls.push({ at: performance.now(), full, subscriptions });
    if (full && httpRefuse) return new Response(JSON.stringify(refusal), { status: 400, headers: { "content-type": "application/json" } });
    let timer, abort;
    const stream = new ReadableStream({
      start(controller) {
        const send = (item, value) => controller.enqueue(new TextEncoder().encode(JSON.stringify({ subscriptionId: item.id, value }) + "\n"));
        if (!full) {
          const topology = subscriptions.find(item => item.path.startsWith("/api/device-topology/state/subscribe"));
          const discovery = subscriptions.find(item => item.path.startsWith("/api/metrics/fields/catalog/subscribe"));
          send(topology, { state: "warming" }); send(discovery, catalog);
          timer = setTimeout(() => { send(topology, { state: warming ? "warming" : "ready" }); controller.close(); }, 25);
        } else if (refuse) {
          const metric = subscriptions.find(item => item.path.startsWith("/api/metrics/subscribe?ids="));
          send(metric, refusal); controller.close();
        } else {
          send(subscriptions[0], { current: true });
          if (++captures === 1) { endedAt.push(performance.now()); controller.close(); }
          else {
            abort = () => controller.error(new DOMException("Aborted", "AbortError"));
            options.signal.addEventListener("abort", abort, { once: true });
          }
        }
      },
      cancel() { clearTimeout(timer); if (abort) options.signal.removeEventListener("abort", abort); }
    });
    return new Response(stream, { headers: { "content-type": "application/x-ndjson" } });
  } };
}

const captureOptions = { baseUrl: "http://127.0.0.1:10421/", durationMs: 1300, readinessTimeoutMs: 5000, keyless: true };

test("failed real slice refusal reproduces the production dashboard selector without the oversized all-id demand", async () => {
  const input = await regressionInput();
  const old = input.events.find(event => event.body?.statusCode === 400);
  assert.deepEqual(old.body, { statusCode: 400, error: "unsupported-subscription-selector" });
  assert.ok(old.path.length > 8192);
  if (process.env.RM_RECORDER_REGRESSION_SLICE) {
    assert.equal(old.path.length, 92664);
    assert.ok(old.path.startsWith("/api/metrics/subscribe?ids=1000000000000001%2Fdisk_all_total_active_percent&ids="));
  }
  const backend = simulatedBackend(input, { refuse: true });
  const { slice, failure } = await captureBackendSlice({ ...captureOptions, fetchImpl: backend.fetchImpl });
  const decoded = metricCatalogPushDecoder.decode(input.events.find(event => event.kind === "frame" && event.path.startsWith("/api/metrics/fields/catalog/subscribe")).body);
  const cards = dashboardPointValues(decoded.metrics).cards;
  const expected = buildMetricSnapshotSubscriptionUrl({ ids: cards.flatMap(card => card.slots.filter(slot =>
    !fieldCardSlot(slot.metricId, slot.binding) && dashboardMetricAvailable(slot.metricId, slot.binding, decoded.metrics)).map(slot => slot.metricId)) }, 1000);
  const actual = backend.calls.find(call => call.full).subscriptions.find(item => item.path.startsWith("/api/metrics/subscribe?ids="));
  const selectors = backend.calls.find(call => call.full).subscriptions.map(item => item.path);
  assert.ok(selectors.every(path => path.length <= 8192), "every selector respects the backend wire limit");
  const settings = dashboardPointValues(decoded.metrics);
  const columns = normalizeResourceTableColumns(settings.resourceTableColumns, decoded.metrics, "software");
  const baseQuery = { bars: [], visibleColumnIds: [], sortColumnId: "", sortDirection: "desc", tableMode: "software", processDetailSoftwareIds: [], residualDetailMetricIds: [] };
  const table = buildResourceMonitorSubscriptionUrl(normalizeResourceMonitorQuery({ ...baseQuery, scope: "table",
    processDetailLevel: columns.some(column => column.visible && ["user", "architecture"].includes(column.id)) ? "full" : "basic",
    sampleMetricIds: ["process.inventory", "process.attribution", ...columns.filter(column => column.visible
      && dashboardMetricAvailable(column.id, column.binding, decoded.metrics)).map(resourceTableMetricId).filter(Boolean)] }), 1000);
  assert.ok(selectors.includes(table));
  const bars = settings.resourceBars.filter(bar => dashboardMetricAvailable(bar.metricId, bar.binding, decoded.metrics)).map(resourceBarQuery);
  assert.ok(selectors.includes(buildResourceMonitorSubscriptionUrl(normalizeResourceMonitorQuery({ ...baseQuery, scope: "bars", bars,
    sampleMetricIds: null, residualDetailMetricIds: resourceBarResidualDetailMetricIds(bars) }), 1000)));
  for (const fields of [numaFieldNames, [effectiveFrequencyFieldName], deviceMetricFieldNames]) assert.ok(selectors.includes(buildMetricFieldSubscriptionUrl({ fields }, 1000)));
  const gpuIndexes = [...new Set(decoded.metrics.flatMap(metric => /^gpu\.(\d+)\./.exec(metric.id)?.slice(1).map(Number) ?? []))];
  if (gpuIndexes.length) assert.ok(selectors.includes(buildGpuSpecializedTelemetrySubscriptionUrl({ counterIds: buildGpuSpecializedCounterIds(gpuIndexes) }, 1000)));
  assert.equal(actual.path, expected);
  assert.ok(actual.path.length < 8192);
  assert.notEqual(actual.path, old.path);
  assert.match(failure.message, /400.*unsupported-subscription-selector/);
  assert.doesNotMatch(failure.message, /ended before/);
  assert.equal(slice.complete, false);
  assert.ok(slice.events.some(event => event.kind === "end" && event.status === 400 && event.path === actual.path && event.body.error === old.body.error));
  assert.equal(backend.calls.filter(call => call.full).length, 1, "a 4xx selector refusal must never reconnect");
  validateSlice(slice);
  if (process.env.RM_RECORDER_EVIDENCE_DIR) await writeFile(resolve(process.env.RM_RECORDER_EVIDENCE_DIR, "recorder-selector-regression.json"),
    JSON.stringify({ rawInput: process.env.RM_RECORDER_REGRESSION_SLICE ?? "synthetic", oldSelectorLength: old.path.length,
      newMetricSelector: actual.path, newMetricSelectorLength: actual.path.length, selectors, readinessWaitedMs: slice.readiness.waitedMs,
      recordedRefusal: slice.events.find(event => event.kind === "end" && event.status === 400) }, null, 2));
});

test("normal stream end reconnects at the production one-second delay and preserves the timed interval after readiness", async () => {
  const backend = simulatedBackend(await regressionInput());
  const { slice, failure } = await captureBackendSlice({ ...captureOptions, fetchImpl: backend.fetchImpl });
  assert.equal(failure, null);
  assert.equal(slice.complete, true);
  assert.ok(slice.readiness.ready && slice.readiness.waitedMs >= 25);
  const captures = backend.calls.filter(call => call.full);
  assert.equal(captures.length, 2);
  assert.ok(captures[1].at - backend.endedAt[0] >= 990);
  assert.ok(slice.events.some(event => event.kind === "end" && event.status === 200 && event.reason === "eof"));
  assert.ok(slice.events.some(event => event.kind === "frame" && event.atMs > 990));
  assert.ok(slice.readiness.events.some(event => event.body?.state === "warming"));
  assert.ok(!slice.events.some(event => event.kind === "frame" && event.body?.state === "warming"));
  validateSlice(slice);
});

test("HTTP refusal records its body, fails promptly, and does not retry", async () => {
  const backend = simulatedBackend(await regressionInput(), { httpRefuse: true });
  const { slice, failure } = await captureBackendSlice({ ...captureOptions, fetchImpl: backend.fetchImpl });
  assert.match(failure.message, /HTTP 400.*unsupported-subscription-selector/);
  assert.ok(slice.events.some(event => event.kind === "end" && event.status === 400 && event.reason === "http-refusal"));
  assert.equal(backend.calls.filter(call => call.full).length, 1);
});

test("warming topology exhausts the readiness bound without starting the timed capture", async () => {
  const backend = simulatedBackend(await regressionInput(), { warming: true });
  const { slice, failure } = await captureBackendSlice({ ...captureOptions, readinessTimeoutMs: 200, fetchImpl: backend.fetchImpl });
  assert.match(failure.message, /Readiness timed out/);
  assert.equal(slice.readiness.ready, false);
  assert.deepEqual(slice.events, []);
  assert.equal(backend.calls.filter(call => call.full).length, 0);
  validateSlice(slice);
});
