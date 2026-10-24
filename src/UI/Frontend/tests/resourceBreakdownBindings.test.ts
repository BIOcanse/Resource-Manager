import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { createResourceBreakdownBindings } from "../src/stores/resourceBreakdownBindings.ts";
import type { PushValueSourceFamily } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import type { ResourceBreakdownQuery, ResourceMonitorQuery } from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceMonitorSnapshot, ResourceProcessDetailsSnapshot } from "../src/types.ts";
import { decodeResourceMonitorWireSnapshot } from "../src/api/resourceBreakdownWire.ts";
import { readyResourceMonitorWire } from "./resourceMonitorFixture.ts";

function source<TQuery, TValue>() {
  const calls: { query: TQuery; interval: number; stopped: boolean; emit: (snapshot: SourceSnapshot<TValue>) => void }[] = [];
  const family: PushValueSourceFamily<TQuery, TValue> = { key: "test", subscribeSnapshot(query, interval, emit) {
    const call = { query, interval, emit, stopped: false }; calls.push(call);
    emit(snapshot<TValue>(null));
    return () => { call.stopped = true; };
  } };
  return { family, calls };
}
function snapshot<T>(data: T | null, error: unknown = null): SourceSnapshot<T> {
  return { key: "test", status: error ? "error" : data ? "ready" : "loading", data, error,
    backendEpoch: "one", revision: 1, acceptedAttempt: null, domainRevision: null, capturedAt: null };
}
const bars = source<ResourceMonitorQuery, ResourceMonitorSnapshot>();
const details = source<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>();
const root = createRoot(dispose => {
  const [active, setActive] = createSignal(true);
  const [intervalMs, setInterval] = createSignal<number | null>(1000);
  const [selected, select] = createSignal<string[]>([]);
  const [scaleMode, setScale] = createSignal<"capacity" | "active">("capacity");
  let pruned = 0;
  const binding = createResourceBreakdownBindings({ active, intervalMs, detailSoftwareIds: selected,
    bars: () => [{ metricId: "memory.usage", scaleMode: scaleMode() }],
    barsSource: bars.family, detailsSource: details.family,
    onBars: () => { selected(); pruned++; }
  });
  return { binding, select, setActive, setInterval, setScale, dispose, pruned: () => pruned };
});
await Promise.resolve();
assert.equal(bars.calls.length, 1);
assert.equal(bars.calls[0].query.scope, "bars");
assert.deepEqual(bars.calls[0].query.processDetailSoftwareIds, []);
assert.equal(details.calls.length, 0);
assert.equal(root.binding.bars()?.data, null);
const barValue = decodeResourceMonitorWireSnapshot(readyResourceMonitorWire());
bars.calls[0].emit(snapshot(barValue));
assert.equal(root.pruned(), 1);

root.select(["software:a"]); await Promise.resolve();
assert.equal(bars.calls.length, 1, "Selection must not recreate the bars binding");
assert.equal(bars.calls[0].stopped, false);
assert.strictEqual(root.binding.bars()?.data, barValue, "Current bars data must stay identical");
assert.equal(details.calls.length, 1);
assert.deepEqual(details.calls[0].query.processDetailSoftwareIds, ["software:a"]);
assert.equal(root.binding.details()?.data, null, "Only the process panel is pending");
const firstDetails: ResourceProcessDetailsSnapshot = { metrics: [{ metricId: "memory.usage", software: [{ softwareId: "software:a", processes: [] }] }] };
details.calls[0].emit(snapshot(firstDetails));
assert.strictEqual(root.binding.details()?.data, firstDetails);

root.select(["software:b"]); await Promise.resolve();
assert.equal(bars.calls.length, 1);
assert.strictEqual(root.binding.bars()?.data, barValue);
assert.equal(details.calls[0].stopped, true);
assert.equal(details.calls.length, 2);
assert.deepEqual(details.calls[1].query.processDetailSoftwareIds, ["software:b"]);
assert.equal(root.binding.details()?.data, null, "Switching software must discard previous details");
details.calls[0].emit(snapshot(firstDetails));
assert.equal(root.binding.details()?.data, null, "A retired binding cannot publish into the new panel");
details.calls[1].emit(snapshot(null, new Error("detail failed")));
assert.strictEqual(root.binding.bars()?.data, barValue, "Detail failure is local to its panel");

root.select(["software:b", "software:a"]); await Promise.resolve();
assert.equal(bars.calls.length, 1, "Expanding additional software changes only details");
root.setScale("active"); await Promise.resolve();
assert.equal(bars.calls.length, 2, "Scale is part of the bars identity");
assert.equal(root.binding.bars()?.data, null, "A new bars binding cannot retain the old value");
root.setInterval(1700); await Promise.resolve();
assert.equal(bars.calls.at(-1)?.interval, 1700);
assert.equal(details.calls.at(-1)?.interval, 1700);
root.setActive(false); await Promise.resolve();
assert.equal(root.binding.bars(), null);
assert.equal(root.binding.details(), null);
assert(bars.calls.every(call => call.stopped)); assert(details.calls.every(call => call.stopped));
root.setActive(true); await Promise.resolve();
assert.equal(root.binding.bars()?.data, null, "Hidden demand retains no metric value");
root.dispose();
console.log("Resource bars and process details use independent current bindings.");
