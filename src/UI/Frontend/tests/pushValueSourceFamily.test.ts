import { MAX_UNSUBSCRIBED_SOURCE_ENTRIES } from "../src/frontendRuntime/source/sourceRetention.ts";
import assert from "node:assert/strict";
import type { HostMessageTransport } from
  "../src/host/hostMessageTransport.ts";
import { BackendSubscriptionChannel } from
  "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";
import { BackendPushValueSource, BackendPushValueSourceFamily } from
  "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import { defineResponseDecoder } from
  "../src/frontendRuntime/request/ResponseDecoder.ts";
import { BackendSessionOwner } from
  "../src/frontendRuntime/session/BackendSessionOwner.ts";

interface SubscriptionRequestItem {
  readonly id: string;
  readonly path: string;
}

interface RecordedStream {
  readonly url: string;
  readonly method: string;
  readonly signal: AbortSignal;
  readonly subscriptions: readonly SubscriptionRequestItem[];
  readonly controller: ReadableStreamDefaultController<Uint8Array>;
}

function createHostTransport() {
  let listener: ((message: unknown) => void) | null = null;
  const transport: HostMessageTransport = {
    canSubscribeMessages: true,
    postMessage: () => undefined,
    subscribeMessages: (next) => {
      listener = next;
      return () => { listener = null; };
    }
  };
  return {
    transport,
    publish: (message: unknown) => listener?.(message)
  };
}

function createStreamHarness() {
  const streams: RecordedStream[] = [];
  const fetch = (async (input, init) => {
    const request = JSON.parse(String(init?.body)) as {
      version: number;
      subscriptions: SubscriptionRequestItem[];
    };
    assert.equal(request.version, 1);
    let streamController: ReadableStreamDefaultController<Uint8Array> | null = null;
    const body = new ReadableStream<Uint8Array>({
      start: (controller) => { streamController = controller; }
    });
    const signal = init?.signal as AbortSignal;
    signal.addEventListener("abort", () => {
      try {
        streamController?.error(signal.reason);
      } catch {
        // A stream that already closed needs no additional abort signal.
      }
    }, { once: true });
    streams.push({
      url: String(input),
      method: String(init?.method),
      signal,
      subscriptions: request.subscriptions,
      controller: streamController!
    });
    return new Response(body, { status: 200 });
  }) as typeof globalThis.fetch;
  return { streams, fetch };
}

function publish(
  stream: RecordedStream,
  subscriptionId: string,
  value: unknown
): void {
  stream.controller.enqueue(new TextEncoder().encode(
    `${JSON.stringify({ subscriptionId, value })}\n`));
}

const readyMessage = (revision: string, epoch: string) => ({
  type: "host.backend-session",
  schemaVersion: 1,
  publicationRevision: revision,
  state: "ready",
  epoch
});

const flush = async () => {
  await new Promise<void>((resolve) => setTimeout(resolve, 0));
  await new Promise<void>((resolve) => setImmediate(resolve));
};

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "independent-decoders"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    fetch: harness.fetch,
    reconnectDelayMs: 1
  });
  const decoder = defineResponseDecoder("test.display-value", (value) => {
    if (!value || typeof (value as { displayValue?: unknown }).displayValue !== "string") {
      throw new Error("Missing displayValue");
    }
    return (value as { displayValue: string }).displayValue;
  });
  const makeSource = (key: string) => new BackendPushValueSource<string>({
    key,
    channel,
    buildUrl: () => `/api/${key}/subscribe`,
    decoder
  });
  const failures: { status: string; data: unknown }[] = [];
  const first: string[] = [];
  const second: string[] = [];
  const releases = [
    makeSource("first").subscribeSnapshot(1_000, (value) => { failures.push(value); if (value.data !== null) first.push(value.data); }),
    makeSource("second").subscribeSnapshot(1_000, (value) => value.data !== null ? second.push(value.data) : undefined)
  ];
  try {
    await flush();
    const stream = harness.streams[0];
    const firstId = stream.subscriptions.find((item) => item.path.includes("/first/"))!.id;
    const secondId = stream.subscriptions.find((item) => item.path.includes("/second/"))!.id;
    publish(stream, secondId, { displayValue: "old" });
    await flush();
    stream.controller.enqueue(new TextEncoder().encode([
      JSON.stringify({ subscriptionId: firstId, value: {} }),
      JSON.stringify({ subscriptionId: secondId, value: { displayValue: "-" } }),
      ""
    ].join("\n")));
    await flush();
    assert.deepEqual(second, ["old", "-"], "A decoder failure must not discard a sibling's empty value");
    assert.deepEqual(first, []);
    assert.equal(failures.at(-1)?.status, "error");
    assert.equal(failures.at(-1)?.data, null, "A decoder failure replaces the affected current value");
    assert.equal(stream.signal.aborted, false);
    assert.equal(harness.streams.length, 1, "A logical decoder must not reconnect the shared stream");
    for (const rejection of [
      { statusCode: 400, error: "unsupported-subscription-selector" },
      { statusCode: 503, error: "access-policy-missing" }
    ]) {
      const siblingValue = `after-${rejection.statusCode}`;
      const failureCount = failures.length;
      stream.controller.enqueue(new TextEncoder().encode([
        JSON.stringify({ subscriptionId: firstId, value: rejection }),
        JSON.stringify({ subscriptionId: secondId, value: { displayValue: siblingValue } }),
        ""
      ].join("\n")));
      await flush();
      assert.equal(failures.length, failureCount + 1, "Each rejection publishes only its affected source result");
      assert.equal(failures.at(-1)?.status, "error", "A rejected selector affects its own source");
      assert.equal(failures.at(-1)?.data, null);
      assert.deepEqual(first, [], "A rejection frame cannot become a current source value");
      assert.equal(second.at(-1), siblingValue, "A rejected selector must not discard its sibling's frame");
      assert.equal(stream.signal.aborted, false, "A rejected selector must not abort the shared stream");
      assert.equal(harness.streams.length, 1, "A rejected selector must not reconnect the shared stream");
    }
    publish(stream, firstId, { displayValue: "recovered" });
    await flush();
    assert.deepEqual(first, ["recovered"]);
  } finally {
    releases.forEach((release) => release());
    channel.dispose();
    owner.dispose();
  }
}

async function waitFor(predicate: () => boolean): Promise<void> {
  const deadline = Date.now() + 1_000;
  while (!predicate()) {
    if (Date.now() >= deadline) {
      throw new Error("Timed out waiting for the subscription transport state.");
    }
    await flush();
  }
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "single-current-value"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    fetch: harness.fetch
  });
  const source = new BackendPushValueSource<number>({
    key: "test.current-value",
    channel,
    buildUrl: (intervalMs) => `/api/current/subscribe?intervalMs=${intervalMs}`,
    decoder: defineResponseDecoder("test.current-number", (value) => Number(value))
  });
  const first: number[] = [];
  const second: number[] = [];
  const releaseFirst = source.subscribeSnapshot(5_000, (value) => value.data !== null ? first.push(value.data) : undefined);
  const releaseSecond = source.subscribeSnapshot(5_000, (value) => value.data !== null ? second.push(value.data) : undefined);
  await flush();
  assert.equal(harness.streams.length, 1);
  assert.equal(harness.streams[0].url, "/api/subscriptions/stream");
  assert.equal(harness.streams[0].method, "POST");
  assert.equal(harness.streams[0].subscriptions.length, 1);
  assert.equal(
    harness.streams[0].subscriptions[0].path,
    "/api/current/subscribe?intervalMs=5000");
  publish(
    harness.streams[0],
    harness.streams[0].subscriptions[0].id,
    7);
  await flush();
  assert.deepEqual(first, [7]);
  assert.deepEqual(second, [7]);
  releaseFirst();
  await flush();
  assert.equal(harness.streams.length, 1);
  releaseSecond();
  await flush();
  assert.equal(harness.streams[0].signal.aborted, true);
  channel.dispose();
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "bodyless-reconnect"));
  let fetchCount = 0;
  const received: number[] = [];
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    reconnectDelayMs: 1,
    fetch: (async (_input, init) => {
      fetchCount += 1;
      if (fetchCount === 1) {
        return new Response(null, { status: 200 });
      }
      if (fetchCount === 2) {
        const request = JSON.parse(String(init?.body)) as {
          subscriptions: SubscriptionRequestItem[];
        };
        const id = request.subscriptions[0].id;
        return new Response(new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(new TextEncoder().encode(
              `${JSON.stringify({ subscriptionId: id, value: 11 })}\n`));
            controller.close();
          }
        }), { status: 200 });
      }
      return new Response(null, { status: 400 });
    }) as typeof fetch
  });
  const source = new BackendPushValueSource<number>({
    key: "test.bodyless-reconnect",
    channel,
    buildUrl: () => "/api/metrics/subscribe",
    decoder: defineResponseDecoder(
      "test.bodyless-number",
      (value) => Number(value))
  });
  const release = source.subscribeSnapshot(1_000, (value) => value.data !== null ? received.push(value.data) : undefined);

  await waitFor(() => fetchCount >= 2 && received.length === 1);
  assert.deepEqual(received, [11]);
  release();
  channel.dispose();
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "serialized-replacement"));
  let fetchCount = 0;
  let activeTransportCount = 0;
  let maximumActiveTransportCount = 0;
  let releaseFirstTransport: (() => void) | null = null;
  const signals: AbortSignal[] = [];
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    reconnectDelayMs: 100,
    fetch: (async (_input, init) => {
      fetchCount += 1;
      activeTransportCount += 1;
      maximumActiveTransportCount = Math.max(
        maximumActiveTransportCount,
        activeTransportCount);
      const signal = init?.signal as AbortSignal;
      signals.push(signal);
      let released = false;
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          const release = () => {
            if (released) {
              return;
            }
            released = true;
            activeTransportCount -= 1;
            controller.close();
          };
          if (fetchCount === 1) {
            releaseFirstTransport = release;
          } else {
            signal.addEventListener("abort", release, { once: true });
          }
        },
        cancel() {
          if (!released) {
            released = true;
            activeTransportCount -= 1;
          }
        }
      });
      return new Response(body, { status: 200 });
    }) as typeof fetch
  });
  const family = new BackendPushValueSourceFamily<{ id: string }, number>({
    key: "test.serialized-replacement",
    channel,
    buildUrl: (_query, intervalMs) =>
      `/api/metrics/subscribe?intervalMs=${intervalMs}`,
    decoder: defineResponseDecoder(
      "test.serialized-number",
      (value) => Number(value))
  });
  const releaseSlow = family.subscribeSnapshot({ id: "cpu" }, 1_000, () => undefined);
  await waitFor(() => fetchCount === 1 && releaseFirstTransport !== null);
  const releaseFast = family.subscribeSnapshot({ id: "cpu" }, 200, () => undefined);
  await waitFor(() => signals[0]?.aborted === true);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(
    fetchCount,
    1,
    "replacement must wait for the old transport body to actually finish");

  releaseFirstTransport!();
  await waitFor(() => fetchCount === 2);
  assert.equal(maximumActiveTransportCount, 1);
  releaseFast();
  releaseSlow();
  await flush();
  channel.dispose();
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "multiplexed"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    fetch: harness.fetch,
    reconnectDelayMs: 10
  });
  const cpu = new BackendPushValueSource<number>({
    key: "test.cpu",
    channel,
    buildUrl: (intervalMs) => `/api/cpu/subscribe?intervalMs=${intervalMs}`,
    decoder: defineResponseDecoder("test.cpu-number", (value) => Number(value))
  });
  const memory = new BackendPushValueSource<number>({
    key: "test.memory",
    channel,
    buildUrl: (intervalMs) => `/api/memory/subscribe?intervalMs=${intervalMs}`,
    decoder: defineResponseDecoder("test.memory-number", (value) => Number(value))
  });
  const cpuValues: number[] = [];
  const memoryValues: number[] = [];
  const releaseCpu = cpu.subscribeSnapshot(1_000, (value) => value.data !== null ? cpuValues.push(value.data) : undefined);
  const releaseMemory = memory.subscribeSnapshot(5_000, (value) => value.data !== null ? memoryValues.push(value.data) : undefined);
  await flush();
  assert.equal(
    harness.streams.length,
    1,
    "independent logical subscriptions share one physical stream");
  assert.equal(harness.streams[0].subscriptions.length, 2);
  const cpuItem = harness.streams[0].subscriptions.find(
    (item) => item.path.startsWith("/api/cpu/"))!;
  const memoryItem = harness.streams[0].subscriptions.find(
    (item) => item.path.startsWith("/api/memory/"))!;
  publish(harness.streams[0], memoryItem.id, 18);
  publish(harness.streams[0], cpuItem.id, 9);
  await flush();
  assert.deepEqual(cpuValues, [9]);
  assert.deepEqual(memoryValues, [18]);

  host.publish(readyMessage("2", "multiplexed"));
  await flush();
  assert.equal(
    harness.streams.length,
    1,
    "a repeated publication for the same epoch keeps the physical stream");
  host.publish(readyMessage("3", "multiplexed-next"));
  await waitFor(() => harness.streams.length === 2);
  assert.equal(harness.streams.length, 2);
  assert.equal(harness.streams[0].signal.aborted, true);
  assert.equal(harness.streams[1].subscriptions.length, 2);

  harness.streams[1].controller.close();
  await new Promise((resolve) => setTimeout(resolve, 30));
  assert.equal(
    harness.streams.length,
    3,
    "a closed physical stream restores the same logical subscription set");
  assert.deepEqual(
    harness.streams[2].subscriptions.map((item) => item.path).sort(),
    harness.streams[1].subscriptions.map((item) => item.path).sort());
  releaseCpu();
  releaseMemory();
  await flush();
  channel.dispose();
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "shared-query"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    fetch: harness.fetch
  });
  const family = new BackendPushValueSourceFamily<{ id: string }, number>({
    key: "test.shared-push",
    channel,
    canonicalizeQuery: (query) => query.id.toLocaleLowerCase(),
    buildUrl: (query, intervalMs) =>
      `/api/metrics/subscribe?id=${query.id}&intervalMs=${intervalMs}`,
    decoder: defineResponseDecoder("test.shared-number", (value) => Number(value))
  });
  const first: number[] = [];
  const second: number[] = [];
  const releaseFirst = family.subscribeSnapshot(
    { id: "cpu" },
    1_000,
    (value) => value.data !== null ? first.push(value.data) : undefined);
  const releaseSecond = family.subscribeSnapshot(
    { id: "CPU" },
    1_000,
    (value) => {
      value.data !== null ? second.push(value.data) : undefined;
      if (value.data !== null) throw new Error("listener failure must remain local");
    });
  await flush();
  assert.equal(harness.streams.length, 1);
  assert.equal(harness.streams[0].subscriptions.length, 1);
  publish(
    harness.streams[0],
    harness.streams[0].subscriptions[0].id,
    3);
  await flush();
  assert.deepEqual(first, [3]);
  assert.deepEqual(second, [3]);

  const releaseFast = family.subscribeSnapshot({ id: "cpu" }, 200, () => undefined);
  await waitFor(() => harness.streams.length === 2);
  assert.equal(harness.streams.length, 2);
  assert.match(harness.streams[1].subscriptions[0].path, /intervalMs=200$/);
  assert.equal(harness.streams[0].signal.aborted, true);

  releaseFast();
  await waitFor(() => harness.streams.length === 3);
  assert.equal(harness.streams.length, 3);
  assert.match(harness.streams[2].subscriptions[0].path, /intervalMs=1000$/);
  assert.equal(harness.streams[1].signal.aborted, true);
  publish(
    harness.streams[2],
    harness.streams[2].subscriptions[0].id,
    4);
  await flush();
  assert.deepEqual(first, [3, 4]);
  assert.deepEqual(second, [3, 4]);

  releaseSecond();
  await flush();
  assert.equal(harness.streams.length, 3);
  releaseFirst();
  await flush();
  assert.equal(harness.streams[2].signal.aborted, true);
  channel.dispose();
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "terminal-status"));
  let fetchCount = 0;
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    reconnectDelayMs: 1,
    fetch: (async () => {
      fetchCount += 1;
      return new Response(null, { status: 400 });
    }) as typeof fetch
  });
  const family = new BackendPushValueSourceFamily<{ id: string }, number>({
    key: "test.terminal-push",
    channel,
    buildUrl: () => "/api/metrics/subscribe",
    decoder: defineResponseDecoder("test.terminal-number", (value) => Number(value))
  });
  const release = family.subscribeSnapshot({ id: "cpu" }, 1_000, () => undefined);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(fetchCount, 1, "a nonretryable response must not reconnect in a loop");
  release();
  channel.dispose();
  owner.dispose();
}

// 授权恢复后显式重连：401 终止、不自己重试；reconnect() 后同一组订阅重新连接并收到值。
{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "reauthorized"));
  const harness = createStreamHarness();
  let authorized = false;
  let rejected = 0;
  const channel = new BackendSubscriptionChannel({
    backendSession: owner,
    reconnectDelayMs: 1,
    fetch: (async (input, init) => {
      if (!authorized) {
        rejected += 1;
        return new Response(null, { status: 401 });
      }
      return harness.fetch(input, init);
    }) as typeof fetch
  });
  const source = new BackendPushValueSource<number>({
    key: "test.reauthorized", channel, buildUrl: () => "/api/current",
    decoder: defineResponseDecoder("test.reauthorized-number", (value) => Number(value))
  });
  const statuses: string[] = [];
  let data: number | null = null;
  const release = source.subscribeSnapshot(1_000, (snapshot) => {
    statuses.push(snapshot.status);
    data = snapshot.data;
  });
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(rejected, 1, "401 ends the stream without retrying");
  assert.equal(statuses.at(-1), "error");

  authorized = true;
  channel.reconnect();
  assert.equal(statuses.at(-1), "error", "reconnecting is demand, not a new value: the current snapshot stays until the stream delivers");
  await flush();
  assert.equal(harness.streams.length, 1, "the unchanged subscription set connects again");
  publish(harness.streams[0], harness.streams[0].subscriptions[0].id, 7);
  await flush();
  assert.equal(statuses.at(-1), "ready");
  assert.equal(data, 7);
  assert.equal(rejected, 1);
  release();
  channel.dispose();
  owner.dispose();
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  const channel = new BackendSubscriptionChannel({ backendSession: owner });
  const source = new BackendPushValueSource<number>({
    key: "test.waiting-session", channel, buildUrl: () => "/api/current",
    decoder: defineResponseDecoder("test.waiting-number", value => Number(value))
  });
  let status = "";
  const release = source.subscribeSnapshot(1000, snapshot => { status = snapshot.status; });
  assert.equal(status, "loading", "Waiting for the host session is pending, not a failed current read");
  release(); channel.dispose(); owner.dispose();
}

// A source exposes exactly its current response, including disconnection and epoch changes.
{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "state-a"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({ backendSession: owner, fetch: harness.fetch, reconnectDelayMs: 1 });
  const source = new BackendPushValueSource<number>({
    key: "test.failure-publication", channel, buildUrl: () => "/api/current",
    decoder: defineResponseDecoder("test.number", (value) => {
      if (typeof value !== "number") throw new Error("Expected a number");
      return value;
    })
  });
  const states: import("../src/frontendRuntime/source/SourceSnapshot.ts").SourceSnapshot<number>[] = [];
  let release = source.subscribeSnapshot(1000, (state) => states.push(state));
  try {
    await waitFor(() => harness.streams.length === 1);
    publish(harness.streams[0], harness.streams[0].subscriptions[0].id, 42);
    await flush();
    assert.equal(states.at(-1)?.data, 42);
    const unchangedPublicationCount = states.length;
    host.publish(readyMessage("2", "state-a"));
    await flush();
    assert.equal(states.length, unchangedPublicationCount,
      "A repeated ready publication for the same session must not invalidate its current value");
    publish(harness.streams[0], harness.streams[0].subscriptions[0].id, "invalid");
    await flush();
    assert.equal(states.at(-1)?.status, "error");
    assert.equal(states.at(-1)?.data, null);
    publish(harness.streams[0], harness.streams[0].subscriptions[0].id, 43);
    await flush();
    harness.streams[0].controller.close();
    await waitFor(() => harness.streams.length === 2);
    assert.equal(states.at(-1)?.status, "error");
    assert.equal(states.at(-1)?.data, null, "Disconnect cannot keep an old healthy value");
    publish(harness.streams[1], harness.streams[1].subscriptions[0].id, 44);
    await flush();
    host.publish(readyMessage("3", "state-b"));
    assert.equal(states.at(-1)?.status, "loading");
    assert.equal(states.at(-1)?.data, null);
    assert.equal(states.at(-1)?.backendEpoch, "state-b");
    await waitFor(() => harness.streams.length === 3);
    host.publish({ type: "host.backend-session", schemaVersion: 1, publicationRevision: "4", state: "unavailable", reasonCode: "connection-lost" });
    assert.equal(states.at(-1)?.status, "unavailable");
    assert.equal(states.at(-1)?.data, null);
    await flush();
    release();
    host.publish(readyMessage("5", "state-c"));
    release = source.subscribeSnapshot(1000, (state) => states.push(state));
    assert.equal(states.at(-1)?.status, "loading");
    assert.equal(states.at(-1)?.data, null, "Epoch change fences even detached entries");
    await waitFor(() => harness.streams.length === 4);
    publish(harness.streams[3], harness.streams[3].subscriptions[0].id, 45);
    await flush();
    assert.equal(states.at(-1)?.data, 45);
    release();
    await flush();
    const beforeReacquire = states.length;
    release = source.subscribeSnapshot(1000, (state) => states.push(state));
    assert.equal(states.length, beforeReacquire + 1);
    assert.equal(states.at(-1)?.status, "ready");
    assert.equal(states.at(-1)?.data, 45, "Demand release retains the latest value");
  } finally {
    release(); channel.dispose(); owner.dispose();
  }
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "retained-family"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({ backendSession: owner, fetch: harness.fetch });
  const family = new BackendPushValueSourceFamily<number, number | null>({
    key: "test.retained-family", channel, buildUrl: id => `/api/current?id=${id}`,
    decoder: defineResponseDecoder("nullable-number", value => {
      if (value === null || typeof value === "number") return value;
      throw new Error("Invalid number");
    })
  });
  const states: import("../src/frontendRuntime/source/SourceSnapshot.ts").SourceSnapshot<number | null>[] = [];
  let release = family.subscribeSnapshot(0, 1000, state => states.push(state));
  try {
    await waitFor(() => harness.streams.length === 1);
    publish(harness.streams[0], harness.streams[0].subscriptions[0].id, 10);
    await flush();
    const accepted = states.at(-1);
    release(); await flush();
    const count = states.length;
    release = family.subscribeSnapshot(0, 1000, state => states.push(state));
    assert.equal(states.length, count + 1);
    assert.strictEqual(states.at(-1), accepted, "No loading frame on demand return");
    await waitFor(() => harness.streams.length === 2);
    publish(harness.streams[1], harness.streams[1].subscriptions[0].id, null);
    await flush();
    assert.equal(states.at(-1)?.status, "ready");
    assert.equal(states.at(-1)?.data, null, "New empty value replaces old value");
    assert((states.at(-1)?.revision ?? 0) > (accepted?.revision ?? 0), "Entry revision survives a transport lease change");
    release(); await flush();
    release = family.subscribeSnapshot(0, 1000, state => states.push(state));
    assert.equal(states.at(-1)?.status, "ready", "Empty value retained");
    await waitFor(() => harness.streams.length === 3);
    publish(harness.streams[2], harness.streams[2].subscriptions[0].id, "failure");
    await flush();
    const failed = states.at(-1);
    assert.equal(failed?.status, "error");
    release(); await flush();
    release = family.subscribeSnapshot(0, 1000, state => states.push(state));
    assert.strictEqual(states.at(-1), failed, "Failure retained, no old-good backfill");
    release();
    host.publish(readyMessage("2", "retained-family-next"));
    release = family.subscribeSnapshot(0, 1000, state => states.push(state));
    assert.equal(states.at(-1)?.status, "loading");
    assert.equal(states.at(-1)?.data, null, "Detached values cannot cross epochs");
  } finally { release(); channel.dispose(); owner.dispose(); }
}

{
  const host = createHostTransport();
  const owner = new BackendSessionOwner({ transport: host.transport });
  host.publish(readyMessage("1", "lru-family"));
  const harness = createStreamHarness();
  const channel = new BackendSubscriptionChannel({ backendSession: owner, fetch: harness.fetch });
  const family = new BackendPushValueSourceFamily<number, number>({
    key: "test.lru", channel, buildUrl: id => `/api/current?id=${id}`,
    decoder: defineResponseDecoder("number", value => Number(value))
  });
  const releases = [];
  let pinned: number | null = null;
  const stopPinned = family.subscribeSnapshot(-1, 1000, state => { pinned = state.data; });
  for (let id = 0; id < MAX_UNSUBSCRIBED_SOURCE_ENTRIES; id++) {
    releases.push(family.subscribeSnapshot(id, 1000, () => undefined));
  }
  try {
    await waitFor(() => harness.streams.length === 1);
    for (const item of harness.streams[0].subscriptions) {
      publish(harness.streams[0], item.id, Number(new URL(item.path, "http://test").searchParams.get("id")));
    }
    await flush();
    releases.forEach(stop => stop());
    let recent: number | null = null;
    family.subscribeSnapshot(0, 1000, state => { recent = state.data; })();
    assert.equal(recent, 0);
    family.subscribeSnapshot(MAX_UNSUBSCRIBED_SOURCE_ENTRIES, 1000, () => undefined)();
    let kept: number | null = null;
    family.subscribeSnapshot(0, 1000, state => { kept = state.data; })();
    assert.equal(kept, 0, "Revisited entry survives count pressure");
    let evicted: number | null = null;
    family.subscribeSnapshot(1, 1000, state => { evicted = state.data; })();
    assert.equal(evicted, null, "Oldest unused entry removed");
    assert.equal(pinned, -1, "Active entry never evicted or overwritten by another query");
  } finally { releases.forEach(stop => stop()); stopPinned(); channel.dispose(); owner.dispose(); }
}
