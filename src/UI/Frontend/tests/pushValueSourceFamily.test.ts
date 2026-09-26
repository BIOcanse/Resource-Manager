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
  const first: string[] = [];
  const second: string[] = [];
  const releases = [
    makeSource("first").subscribe(1_000, (value) => first.push(value)),
    makeSource("second").subscribe(1_000, (value) => second.push(value))
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
    assert.equal(stream.signal.aborted, false);
    assert.equal(harness.streams.length, 1, "A logical decoder must not reconnect the shared stream");
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
  const releaseFirst = source.subscribe(5_000, (value) => first.push(value));
  const releaseSecond = source.subscribe(5_000, (value) => second.push(value));
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
  const release = source.subscribe(1_000, (value) => received.push(value));

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
  const releaseSlow = family.subscribe({ id: "cpu" }, 1_000, () => undefined);
  await waitFor(() => fetchCount === 1 && releaseFirstTransport !== null);
  const releaseFast = family.subscribe({ id: "cpu" }, 200, () => undefined);
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
  const releaseCpu = cpu.subscribe(1_000, (value) => cpuValues.push(value));
  const releaseMemory = memory.subscribe(5_000, (value) => memoryValues.push(value));
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
  const releaseFirst = family.subscribe(
    { id: "cpu" },
    1_000,
    (value) => first.push(value));
  const releaseSecond = family.subscribe(
    { id: "CPU" },
    1_000,
    (value) => {
      second.push(value);
      throw new Error("listener failure must remain local");
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

  const releaseFast = family.subscribe({ id: "cpu" }, 200, () => undefined);
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
  const release = family.subscribe({ id: "cpu" }, 1_000, () => undefined);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(fetchCount, 1, "a nonretryable response must not reconnect in a loop");
  release();
  channel.dispose();
  owner.dispose();
}
