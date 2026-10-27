import assert from "node:assert/strict";
import { PushCurrentItems, type PushValueItemPolicy } from "../src/frontendRuntime/push/PushCurrentItems.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import { metricCurrentItems } from "../src/data/monitor/metricCurrentItems.ts";
import { metricFieldCurrentItems } from "../src/data/metricFields/metricFieldCurrentItems.ts";
import { gpuSpecializedCurrentItems } from "../src/data/gpu/gpuSpecializedCurrentItems.ts";
import { resourceMonitorCurrentItems, resourceDetailsCurrentItems } from "../src/data/resourceMonitor/resourceCurrentItems.ts";
import type { ResourceMonitorQuery } from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceBreakdownBar, ResourceMonitorSnapshot } from "../src/types.ts";

const time = (second: number) => `2026-10-27T00:00:${String(second).padStart(2, "0")}Z`;
function harness<Q, V>(policy: PushValueItemPolicy<Q, V>) {
  const owner = new PushCurrentItems(policy);
  let revision = 0;
  const state = (data: V | null, second: number | null, error: unknown = null): SourceSnapshot<V> => ({
    key: "test", backendEpoch: "one", status: error ? "error" : "ready", data, error,
    revision: ++revision, acceptedAttempt: null, domainRevision: null, capturedAt: second === null ? null : time(second)
  });
  const pending: SourceSnapshot<V> = { ...state(null, null), status: "loading" };
  return { owner, state, pending, read: (query: Q) => owner.project(query, pending) };
}

// Per-item capture times, rather than a later envelope or receipt, decide ordering.
{
  const h = harness(metricCurrentItems);
  const query = { ids: ["a", "b"] };
  const unansweredFrame = h.state({ version: 5, catalogRevision: "one", capturedAt: null, items: {} }, null);
  h.owner.accept(query, unansweredFrame);
  const unanswered = h.owner.project(query, unansweredFrame);
  assert.equal(unanswered.status, "loading", "An empty frame cannot answer a cold requested metric ID");
  assert.deepEqual(unanswered.data?.items, {}, "Never fabricate Invalid or any other backend item");
  const frame = (capturedAt: string, value: number, observedAt: string) => ({ version: 5 as const, catalogRevision: "one", capturedAt,
    items: { a: { numericValue: value, observedAt } } });
  h.owner.accept({ ids: ["a"] }, h.state(frame(time(20), 20, time(20)), 20));
  h.owner.accept(query, h.state(frame(time(40), 10, time(10)), 40));
  assert.equal(h.read(query).data?.items.a.numericValue, 20);
  assert.equal(h.read(query).status, "loading", "A requested ID not yet answered stays pending");
  assert.equal(h.read(query).data?.items.b, undefined, "Absence never creates an Invalid backend value");
  assert(Object.values(h.read(query).data!.items).every(item => item.status !== 0));
  h.owner.accept(query, h.state({ ...frame(time(41), 0, time(41)), items: {} }, 41));
  assert.equal(h.read(query).data?.items.a.numericValue, 20, "A successful frame's omission does not clear an answered metric");
  assert.equal(h.read(query).status, "loading");
  const backendPlaceholder = { id: "b", status: 7, numericValue: null, observedAt: null };
  h.owner.accept({ ids: ["b"] }, h.state({ version: 5, catalogRevision: "one", capturedAt: null,
    items: { b: backendPlaceholder } }, null));
  assert.strictEqual(h.read(query).data?.items.b, backendPlaceholder, "Only the backend's real Warming placeholder answers the ID");
  assert.equal(h.read(query).status, "ready", "Answered Warming is distinct from never answered");
  h.owner.accept({ ids: ["a"] }, h.state(null, null, new Error("failed")));
  assert.equal(h.read(query).data?.items.a, undefined, "Failure cannot backfill the old good item");
  assert.equal(h.read(query).status, "error");
  h.owner.accept({ ids: ["a"] }, h.state(frame(time(15), 15, time(15)), 15));
  assert.equal(h.read(query).data?.items.a, undefined, "An old frame cannot resurrect pre-failure data");
  h.owner.accept({ ids: ["a"] }, h.state(frame(time(50), 50, time(50)), 50));
  assert.equal(h.read(query).status, "ready");
  assert.equal(h.read(query).data?.items.a.numericValue, 50);
  h.owner.accept({ ids: ["a"] }, h.state(frame(time(50), 51, "2026-10-27T00:00:50.0000001Z"), 50));
  assert.equal(h.read(query).data?.items.a.numericValue, 51, "Backend sub-millisecond captures remain ordered");
  h.owner.accept({ ids: ["a"] }, h.state(frame(time(50), 49, "2026-10-27T00:00:50.0000000Z"), 50));
  assert.equal(h.read(query).data?.items.a.numericValue, 51);
  h.owner.retain([{ ids: ["b"] }]);
  assert.equal(h.read({ ids: ["a"] }).data?.items.a, undefined, "Eviction releases unreferenced item values");
}

{
  const h = harness(metricFieldCurrentItems);
  const devices = (value: number, second: number, identity = "cpu0") => [{ deviceIdentity: identity, kind: "cpu", address: null,
    displayName: null, ordinal: 0, fields: [{ name: "usage", unit: "%", valueKind: "float64", reading: null,
      numericValue: value, integerValue: null, booleanValue: null, observedAt: time(second) }] }];
  const full = { fields: ["usage", "temperature"] };
  h.owner.accept({ fields: ["usage"] }, h.state(devices(10, 10), null));
  h.owner.accept({ fields: ["usage"] }, h.state(devices(20, 20), null));
  assert.equal(h.read(full).data?.[0].fields[0].numericValue, 20);
  h.owner.accept({ fields: ["usage"] }, h.state(devices(15, 15), null));
  assert.equal(h.read(full).data?.[0].fields[0].numericValue, 20);
  assert.equal(h.read({ fields: ["temperature"] }).status, "loading");
  assert.equal(h.read(full).status, "loading", "Known fields do not answer another pending field");
  h.owner.accept({ fields: ["usage"] }, h.state([], null));
  assert.deepEqual(h.read(full).data, [], "An accepted empty device list removes missing devices across queries");
}

{
  const h = harness(gpuSpecializedCurrentItems);
  const frame = (second: number, value: number) => ({ version: 3 as const, capturedAt: time(second), adapters: [{
    adapterIndex: 0, adapterName: "GPU", vendorId: "test", architecture: "test", counters: [{ counterId: "gpu.0.cuda",
      counterClass: 0, value, unit: "%", providerId: "test", isIntrusive: false }] }] });
  const full = { counterIds: ["gpu.0.cuda", "gpu.0.rt"] };
  h.owner.accept(full, h.state(frame(10, 10), 10));
  h.owner.accept({ counterIds: ["gpu.0.cuda"] }, h.state(frame(20, 20), 20));
  h.owner.accept(full, h.state(frame(15, 15), 15));
  assert.equal(h.read(full).data?.adapters[0].counters[0].value, 20);
  assert.equal(h.read({ counterIds: ["gpu.0.cuda", "gpu.0.new"] }).status, "loading", "Known counters do not answer a pending counter");
  assert.equal(h.read({ counterIds: ["gpu.1.cuda"] }).data, null, "Counter and adapter identity stay isolated");
}

const bar = (metricId: string, second: number): ResourceBreakdownBar => ({
  metricId, unit: "%", processFieldName: null, scaleMode: "capacity", capturedAt: time(second), totalMetricId: metricId,
  totalValue: second, processSumValue: second - 1, residualValue: 1, additiveAcrossProcesses: true,
  windowMilliseconds: second * 100, quantity: null, unavailableReason: null, gpuEngine: null, engineClass: null,
  capacityValue: 100, totalSystemPercent: second, software: []
});
const query = (scope: "table" | "bars", ids: string[]): ResourceMonitorQuery => ({
  scope, bars: ids.map(metricId => ({ metricId, scaleMode: "capacity" })), processDetailSoftwareIds: [], residualDetailMetricIds: [],
  sampleMetricIds: ids, visibleColumnIds: [], sortColumnId: "impact", sortDirection: "desc", tableMode: "software"
});
function resourceFrame(scope: "table" | "bars", second: number, ids: string[]): ResourceMonitorSnapshot {
  const metrics = ids.map(id => bar(id, second));
  return scope === "bars"
    ? { scope, capturedAt: time(second), breakdown: { capturedAt: time(second), bars: metrics } }
    : { scope, capturedAt: time(second), facts: { capturedAt: time(second), metrics, identities: [] } };
}
for (const scope of ["table", "bars"] as const) {
  const h = harness(resourceMonitorCurrentItems);
  const full = query(scope, ["cpu", "memory"]);
  const narrow = query(scope, ["memory"]);
  h.owner.accept(full, h.state(resourceFrame(scope, 10, ["cpu", "memory"]), 10));
  h.owner.accept(narrow, h.state(resourceFrame(scope, 20, ["memory"]), 20));
  h.owner.accept(full, h.state(resourceFrame(scope, 15, ["cpu", "memory"]), 15));
  const value = h.read(full).data!;
  const metric = (scope === "bars" ? value.breakdown.bars : value.facts?.metrics)!.find(item => item.metricId === "memory")!;
  assert.equal(metric.totalValue, 20, `${scope} overlapping metric retains current totals`);
  assert.equal(metric.windowMilliseconds, 2000, "Window and totals stay atomic with the selected metric");
  assert.equal(metric.processSumValue, 19);
  const partial = query(scope, ["memory", "new"]);
  assert.equal(h.read(partial).status, "loading", `${scope}: known metrics cannot answer a pending item`);
  h.owner.accept(partial, h.state(resourceFrame(scope, 21, ["memory"]), 21));
  assert.equal(h.read(partial).status, "loading", `${scope}: a missing numeric item is not an unavailable placeholder`);
  const unavailableFrame = resourceFrame(scope, 21, ["memory", "new"]);
  const unavailable = (scope === "bars" ? unavailableFrame.breakdown.bars : unavailableFrame.facts?.metrics)!.find(item => item.metricId === "new")!;
  Object.assign(unavailable, { totalValue: null, capacityValue: null, totalSystemPercent: null,
    processSumValue: null, residualValue: null, unavailableReason: "hardware-source-unavailable", software: [] });
  h.owner.accept(partial, h.state(unavailableFrame, 21));
  assert.equal(h.read(partial).status, "ready", `${scope}: a real backend unavailable item answers the query`);
  assert.equal(h.read(query(scope === "table" ? "bars" : "table", ["memory"])).data, null, "Table and bars have independent contracts");
  if (scope === "bars") {
    const storedCase = { ...full, bars: full.bars!.map(item => ({ ...item, scaleMode: "Capacity" as typeof item.scaleMode })) };
    assert.equal(h.read(storedCase).data?.breakdown.bars?.find(item => item.metricId === "memory")?.totalValue, 21,
      "Persisted scale spelling and the decoded wire identify the same bar");
    assert.equal(h.read({ ...full, processDetailSoftwareIds: ["software:a"] }).data?.breakdown.bars?.find(item => item.metricId === "memory")?.totalValue,
      21, "Detail selections do not create a second cache of a bar's totals");
    assert.equal(h.read({ ...full, bars: [{ metricId: "memory", scaleMode: "active" }] }).data, null, "Scale cannot borrow a different representation");
  } else {
    const basic = { ...full, sampleMetricIds: [...full.sampleMetricIds!, "process.inventory"] };
    const detailed = { ...basic, processDetailLevel: "full" as const };
    assert.equal(h.read(detailed).status, "loading", "Full identities stay pending while metric current values are immediately readable");
    h.owner.accept(detailed, h.state(resourceFrame(scope, 16, ["cpu", "memory"]), 16));
    h.owner.accept(basic, h.state(resourceFrame(scope, 25, ["memory"]), 25));
    assert.equal(h.read(detailed).data?.facts?.metrics.find(item => item.metricId === "memory")?.totalValue, 25,
      "A later basic response also updates the metric projected into a full-detail query");
  }
  h.owner.accept(narrow, h.state(resourceFrame(scope, 30, []), 30));
  const retained = h.read(full).data!;
  assert.equal((scope === "bars" ? retained.breakdown.bars : retained.facts?.metrics)!.find(item => item.metricId === "memory")?.totalValue,
    scope === "bars" ? 21 : 25, "An omitted numeric item cannot erase its current value");
  const empty = resourceFrame(scope, 31, ["memory"]);
  const memory = (scope === "bars" ? empty.breakdown.bars : empty.facts?.metrics)![0];
  Object.assign(memory, { totalValue: null, capacityValue: null, totalSystemPercent: null,
    processSumValue: null, residualValue: null, unavailableReason: "hardware-source-unavailable", software: [] });
  h.owner.accept(narrow, h.state(empty, 31));
  const answered = h.read(full).data!;
  assert.equal((scope === "bars" ? answered.breakdown.bars : answered.facts?.metrics)!.find(item => item.metricId === "memory")?.totalValue,
    null, "A real unavailable numeric payload replaces the current value");
}

{
  const h = harness(resourceDetailsCurrentItems);
  const full = { ...query("bars", ["cpu", "memory"]), processDetailSoftwareIds: ["software:a"] };
  const narrow = { ...full, bars: [{ metricId: "memory", scaleMode: "capacity" as const }] };
  const frame = (second: number, ids: string[]) => ({ capturedAt: time(second), metrics: ids.map(id => ({ ...bar(id, second),
    software: [{ softwareId: "software:a", processes: [] }] })) });
  h.owner.accept(full, h.state(frame(10, ["cpu", "memory"]), 10));
  h.owner.accept(narrow, h.state(frame(20, ["memory"]), 20));
  h.owner.accept(full, h.state(frame(15, ["cpu", "memory"]), 15));
  assert.equal(h.read(full).data?.metrics.find(item => item.metricId === "memory")?.totalValue, 20);
  const other = h.read({ ...narrow, processDetailSoftwareIds: ["software:b"] });
  assert.equal(other.status, "loading", "Other software details stay pending");
  assert.deepEqual(other.data?.metrics[0].software, [], "Known metric totals do not fabricate unreceived software processes");

  const selected = { ...narrow, processDetailSoftwareIds: ["software:a", "software:b"] };
  const softwareFrame = (second: number, ids: string[]) => ({ capturedAt: time(second), metrics: [{ ...bar("memory", second),
    software: ids.map(softwareId => ({ softwareId, processes: [{ processId: softwareId === "software:a" ? 1 : 2,
      processStartKey: "100", name: softwareId, value: second, systemPercent: second, softwarePercent: 100 }] })) }] });
  h.owner.accept(selected, h.state(softwareFrame(30, selected.processDetailSoftwareIds), 30));
  h.owner.accept({ ...narrow, processDetailSoftwareIds: ["software:b"] }, h.state(softwareFrame(40, ["software:b"]), 40));
  const rows = h.read(selected).data!.metrics[0].software;
  assert.equal(rows.find(item => item.softwareId === "software:b")?.processes[0].value, 40,
    "AB -> B -> AB retains B's newer processes across selection queries");
  assert.equal(rows.find(item => item.softwareId === "software:a")?.processes[0].value, 30,
    "A software omitted from the sampling demand keeps its accepted process set");
  h.owner.accept(selected, h.state(softwareFrame(35, selected.processDetailSoftwareIds), 35));
  assert.equal(h.read(selected).data?.metrics[0].software.find(item => item.softwareId === "software:b")?.processes[0].value, 40);
  h.owner.accept({ ...narrow, processDetailSoftwareIds: ["software:b"] }, h.state(softwareFrame(50, []), 50));
  assert(!h.read(selected).data?.metrics[0].software.some(item => item.softwareId === "software:b"), "A complete empty software result replaces old processes");

  const sourced = { bars: [{ metricId: "memory.usage", source: "private-working-set", scaleMode: "capacity" as const }],
    processDetailSoftwareIds: [], residualDetailMetricIds: ["memory.usage"] };
  const residualFrame = (second: number) => ({ capturedAt: time(second), metrics: [{ ...bar("private-working-set", second),
    totalMetricId: "memory.usage", software: [{ softwareId: "opaque-residual", processes: [{ processId: -1, name: "kernel",
      value: second, systemPercent: second, softwarePercent: 100 }] }] }] });
  h.owner.accept(sourced, h.state(residualFrame(30), 30));
  const alias = { ...sourced, residualDetailMetricIds: ["private-working-set"] };
  h.owner.accept(alias, h.state(residualFrame(40), 40));
  assert.equal(h.read(sourced).status, "ready");
  assert.equal(h.read(sourced).data?.metrics[0].software[0].processes[0].value, 40,
    "A sourced residual is selected by its totalMetricId or its field ID without separate current values");
}
console.log("Push item policies: metrics, device fields, GPU counters, facts, bars and details passed.");
