import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { usePageRefreshScheduler } from "../src/app/usePageRefreshScheduler.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import { BackendPushValueSource } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { defineResponseDecoder } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import type { LocalSystemStatus } from "../src/data/localSystem/localSystemStatusDecoder.ts";
import type { ResourceManagerSelfSchedulingSnapshot } from "../src/data/selfScheduling/resourceManagerSelfSchedulingDecoder.ts";
import type { FrontendRefreshIntervals } from "../src/types.ts";
import { frontendRefreshIntervals } from "./fixtures/frontendRefreshIntervalFixture.ts";

// Run the real owner and channel with browser Solid scheduling, without a DOM.
Object.assign(globalThis, {
  document: { hasFocus: () => true },
  window: {
    addEventListener() {}, removeEventListener() {},
    requestAnimationFrame: () => 0, cancelAnimationFrame() {}
  }
});
const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const streams: { signal: AbortSignal; publish: (path: string, value: unknown) => void }[] = [];
let inFlight = 0;
let maxInFlight = 0;
const channel = new BackendSubscriptionChannel({
  backendSession: session,
  fetch: (async (_input, init) => {
    const { subscriptions } = JSON.parse(String(init?.body)) as {
      subscriptions: { id: string; path: string }[];
    };
    const signal = init!.signal!;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        maxInFlight = Math.max(maxInFlight, ++inFlight);
        signal.addEventListener("abort", () => {
          --inFlight;
          controller.error(signal.reason);
        }, { once: true });
        streams.push({ signal, publish(path, value) {
          const item = subscriptions.find(item => item.path.startsWith(path));
          assert.ok(item);
          controller.enqueue(new TextEncoder().encode(
            JSON.stringify({ subscriptionId: item.id, value }) + "\n"));
        } });
      }
    });
    return new Response(body);
  }) as typeof fetch
});
const source = <T>(key: string) => new BackendPushValueSource<T>({
  key, channel, buildUrl: interval => `/api/${key}/subscribe?intervalMs=${interval}`,
  decoder: defineResponseDecoder(key, value => value as T)
});
const [visible, setVisible] = createSignal(true);
const [settings, setSettings] = createSignal<FrontendRefreshIntervals | null>(frontendRefreshIntervals);
let dispose = () => {};
const scheduler = createRoot(stop => {
  dispose = stop;
  return usePageRefreshScheduler({
    operations: { subscribeInvalidations: () => () => {} },
    activePage: () => "settings",
    selfSchedulingSource: source<ResourceManagerSelfSchedulingSnapshot>("scheduling"),
    localSystemSource: source<LocalSystemStatus>("local-system"),
    frontendWork: { frontendVisible: visible, activeWorkIds: () => [], isNeeded: () => false },
    runtimeCapabilities: { runtimeEffectsEnabled: () => false, optimizationEnabled: () => false },
    refreshIntervals: settings,
    management: { activeSubpage: () => "Dependency", refreshState() {},
      refreshBrowserRuntimes() {}, refreshComponents() {}, refreshSoftware() {} },
    optimization: { refreshReports() {}, refreshHostManagerStatus() {} }
  });
});
const flush = async () => {
  await new Promise(resolve => setTimeout(resolve, 10));
};
try {
  await flush();
  assert.equal(streams.length, 1);
  streams[0].publish("/api/local-system/", { uptimeSeconds: 3600 });
  streams[0].publish("/api/scheduling/", { cpuGrade: "normal" });
  await flush();
  assert.equal(scheduler.systemUptime(), "01:00:00");

  // Each language save publishes a fresh settings result with the same cadence.
  for (let language = 0; language < 9; language++) {
    setSettings(structuredClone(frontendRefreshIntervals));
    await flush();
    assert.equal(streams.length, 1, "equivalent settings must keep the physical channel");
    assert.equal(streams[0].signal.aborted, false);
    assert.equal(scheduler.systemUptime(), "01:00:00");
  }
  const changed = structuredClone(frontendRefreshIntervals);
  (changed.foreground as { monitor: number }).monitor += 1000;
  setSettings(changed);
  await flush();
  assert.equal(streams.length, 2, "a real cadence change must replace the binding");
  assert.equal(streams[0].signal.aborted, true);
  assert.deepEqual(scheduler.selfScheduling(), { cpuGrade: "normal" }, "Cadence change retains the same entry current value");

  setVisible(false);
  await flush();
  assert.equal(inFlight, 0, "hidden window must release all subscriptions immediately");
  assert.equal(scheduler.systemUptime(), "01:00:00");
  setVisible(true);
  await flush();
  assert.equal(streams.length, 3);
  assert.deepEqual(scheduler.selfScheduling(), { cpuGrade: "normal" });
  assert.equal(scheduler.systemUptime(), "01:00:00", "Reactivation synchronously displays the current value");
  assert.equal(maxInFlight, 1);
} finally {
  dispose(); channel.dispose(); session.dispose();
}
assert.equal(inFlight, 0);
console.log("Page refresh subscriptions: equivalent settings retain one stream; cadence and visibility replace current bindings.");
