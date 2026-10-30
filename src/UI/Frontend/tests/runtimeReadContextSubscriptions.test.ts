import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { useRuntimeReadContext } from "../src/app/useRuntimeReadContext.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import { BackendPushValueSource, type PushValueSource } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { defineResponseDecoder } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import type { LocalSystemStatus } from "../src/data/localSystem/localSystemStatusDecoder.ts";
import type { FrontendRefreshIntervals } from "../src/types.ts";
import { frontendRefreshIntervals } from "./fixtures/frontendRefreshIntervalFixture.ts";

// Run the real owner and channel with browser Solid scheduling, without a DOM.
const focusListeners = new Map<string, () => void>();
Object.assign(globalThis, {
  document: { hasFocus: () => true },
  window: {
    addEventListener(event: string, listener: () => void) { focusListeners.set(event, listener); },
    removeEventListener(event: string) { focusListeners.delete(event); },
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
const bindings: { key: string; intervalMs: number; released: boolean }[] = [];
function source<T>(key: string): PushValueSource<T> {
  const shared = new BackendPushValueSource<T>({
    key, channel, buildUrl: interval => `/api/${key}/subscribe?intervalMs=${interval}`,
    decoder: defineResponseDecoder(key, value => value as T)
  });
  return { key, readSnapshot: () => shared.readSnapshot(), subscribeSnapshot(intervalMs, listener) {
    const binding = { key, intervalMs, released: false };
    bindings.push(binding);
    const unsubscribe = shared.subscribeSnapshot(intervalMs, listener);
    return () => { binding.released = true; unsubscribe(); };
  } };
}
const localBindings = () => bindings.filter(binding => binding.key === "local-system");
const savedSettings = structuredClone(frontendRefreshIntervals);
// This is the journey's saved custom choice.
(savedSettings.resolved as { localSystem: number }).localSystem = 1000;
const [visible, setVisible] = createSignal(true);
const [settings, setSettings] = createSignal<FrontendRefreshIntervals | null>(null);
let dispose = () => {};
const readContext = createRoot(stop => {
  dispose = stop;
  return useRuntimeReadContext({
    localSystemSource: source<LocalSystemStatus>("local-system"),
    frontendWork: { frontendVisible: visible },
    refreshIntervals: settings
  });
});
const flush = async () => {
  await new Promise(resolve => setTimeout(resolve, 10));
};
const waitForStreams = async (count: number) => {
  const deadline = Date.now() + 1000;
  while (streams.length !== count && Date.now() < deadline) await flush();
};
try {
  await flush();
  assert.equal(streams.length, 0, "startup waits for the saved refresh preferences");
  assert.equal(bindings.length, 0, "the owner opens no provisional subscription");
  setSettings(savedSettings);
  await flush();
  assert.equal(streams.length, 1);
  assert.deepEqual(localBindings(), [{ key: "local-system", intervalMs: 1000, released: false }]);
  assert.deepEqual(bindings.map(binding => binding.key), ["local-system"], "no other read subscription is opened");
  streams[0].publish("/api/local-system/", { uptimeSeconds: 3600 });
  await flush();
  assert.equal(readContext.systemUptime(), "01:00:00");
  assert.deepEqual(readContext.readIntervals(), savedSettings.resolved, "the read intervals are the manual settings");

  // Focus changes never change the read intervals, so the local owner keeps its lease.
  for (let round = 0; round < 2; round++) {
    for (const event of ["blur", "focus"]) {
      assert.ok(focusListeners.has(event));
      focusListeners.get(event)!();
      await flush();
      assert.equal(streams.length, 1);
      assert.deepEqual(localBindings(), [{ key: "local-system", intervalMs: 1000, released: false }],
        `${event} must not replace the local-system subscription`);
      assert.deepEqual(readContext.readIntervals(), savedSettings.resolved);
    }
  }

  // Each language save publishes a fresh settings result with the same cadence.
  for (let language = 0; language < 9; language++) {
    setSettings(structuredClone(savedSettings));
    await flush();
    assert.equal(streams.length, 1, "equivalent settings must keep the physical channel");
    assert.equal(streams[0].signal.aborted, false);
    assert.equal(readContext.systemUptime(), "01:00:00");
    assert.equal(localBindings().length, 1);
    assert.equal(localBindings()[0].released, false);
  }
  const changed = structuredClone(savedSettings);
  (changed.resolved as { monitor: number }).monitor += 1000;
  setSettings(changed);
  await flush();
  assert.equal(readContext.readIntervals()!.monitor, changed.resolved.monitor, "a manual change takes effect directly");
  assert.equal(streams.length, 1, "a sibling's cadence does not touch the local-system transport");
  assert.deepEqual(localBindings(), [{ key: "local-system", intervalMs: 1000, released: false }],
    "a sibling's cadence does not replace the local owner lease");

  const localChanged = structuredClone(changed);
  (localChanged.resolved as { localSystem: number }).localSystem = 2300;
  setSettings(localChanged);
  await waitForStreams(2);
  assert.equal(streams.length, 2);
  assert.equal(streams[0].signal.aborted, true);
  assert.deepEqual(localBindings(), [
    { key: "local-system", intervalMs: 1000, released: true },
    { key: "local-system", intervalMs: 2300, released: false }
  ], "a genuine local cadence change replaces exactly one owner binding");

  setVisible(false);
  await flush();
  assert.equal(inFlight, 0, "hidden window must release all subscriptions immediately");
  assert.ok(bindings.every(binding => binding.released));
  assert.equal(readContext.systemUptime(), "01:00:00");
  setVisible(true);
  await waitForStreams(3);
  assert.equal(streams.length, 3);
  assert.equal(localBindings().length, 3);
  assert.deepEqual(localBindings()[2], { key: "local-system", intervalMs: 2300, released: false });
  assert.equal(readContext.systemUptime(), "01:00:00", "Reactivation synchronously displays the current value");
  assert.equal(maxInFlight, 1);
} finally {
  dispose(); channel.dispose(); session.dispose();
}
assert.equal(inFlight, 0);
assert.ok(bindings.every(binding => binding.released));
assert.equal(focusListeners.size, 0);
console.log("Runtime read subscriptions: saved startup cadence opens once; the read intervals are the manual settings; equivalent settings, focus and sibling cadence changes retain the lease; local cadence and visibility replace it.");
