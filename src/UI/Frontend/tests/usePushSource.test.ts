import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { usePushSource } from "../src/frontendRuntime/push/usePushSource.ts";
import { BackendPushValueSourceFamily } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import type { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import { defineResponseDecoder } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";

const listeners: { path: string; stopped: boolean; publish: (value: SourceSnapshot<number>) => void }[] = [];
const channel = {
  backendEpoch: "one",
  subscribe(_key: string, path: string, _decoder: unknown, publish: (value: SourceSnapshot<number>) => void) {
    const entry = { path, publish, stopped: false }; listeners.push(entry);
    return { updatePath(next: string) { entry.path = next; }, dispose() { entry.stopped = true; } };
  }
} as unknown as BackendSubscriptionChannel;
let canonicalizations = 0;
const family = new BackendPushValueSourceFamily<string, number>({
  key: "test.hook", channel, buildUrl: (query, interval) => `${query}/${interval}`,
  canonicalizeQuery: query => { canonicalizations++; return query; },
  decoder: defineResponseDecoder("number", value => Number(value))
});
const ready = (data: number): SourceSnapshot<number> => ({ key: family.key, status: "ready", data, error: null,
  backendEpoch: "one", revision: 1, acceptedAttempt: null, domainRevision: null, capturedAt: null });
for (const [query, value] of [["a", 42], ["b", 17]] as const) {
  const stop = family.subscribeSnapshot(query, 1000, () => {});
  listeners.at(-1)!.publish(ready(value)); stop();
}
const state = createRoot(dispose => {
  const [active, setActive] = createSignal(false);
  const [query, setQuery] = createSignal("a");
  const [interval, setInterval] = createSignal(1000);
  const [unrelated, setUnrelated] = createSignal(0);
  const view = usePushSource(family, () => {
    unrelated(); return { active: active(), query: query(), intervalMs: interval() };
  });
  assert.strictEqual(view.snapshot(), family.readSnapshot("a"), "First value exists before effects and demand");
  assert.equal(view.snapshot().data, 42);
  assert.equal(listeners.length, 2);
  setQuery("b");
  assert.equal(view.snapshot().data, 17, "Query switch reads immediately before effects");
  setQuery("cold"); assert.equal(view.snapshot().status, "loading");
  setQuery("a");
  return { dispose, view, setActive, setQuery, setInterval, setUnrelated };
});
try {
  assert.equal(listeners.length, 2, "Inactive hook creates no demand");
  state.setActive(true);
  assert.equal(listeners.length, 3);
  state.setUnrelated(1);
  assert.equal(listeners.length, 3, "Equivalent demand retains its lease");
  // The held demand was canonicalized when it was selected; reads and a published frame do not canonicalize it again.
  const selectedCanonicalizations = canonicalizations;
  for (let read = 0; read < 5; read++) state.view.snapshot();
  listeners[2].publish(ready(43));
  assert.equal(state.view.snapshot().data, 43);
  assert.equal(canonicalizations, selectedCanonicalizations, "Reads of a held demand do not canonicalize its query");
  state.setInterval(2300);
  assert.equal(listeners.length, 4);
  assert.equal(listeners[2].stopped, true);
  state.setQuery("b");
  assert.equal(state.view.snapshot().data, 17);
  listeners[3].publish(ready(99));
  assert.equal(state.view.snapshot().data, 17, "Late old query cannot overwrite the selected entry");
  state.setActive(false);
  assert.equal(state.view.snapshot().data, 17);
  assert(listeners.every(item => item.stopped));
} finally { state.dispose(); }
console.log("Push hook: warm first read, inactive reads, query isolation, equal demand and cleanup passed.");
