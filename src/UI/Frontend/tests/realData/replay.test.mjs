import assert from "node:assert/strict";
import { createSliceReplay, coversSelector } from "./replaySlice.mjs";

assert.equal(coversSelector("/api/metrics/subscribe?ids=cpu.usage&ids=memory.usage&intervalMs=1000", "/api/metrics/subscribe?ids=cpu.usage&intervalMs=2000"), true);
assert.equal(coversSelector("/api/metrics/subscribe?ids=cpu.usage", "/api/metrics/subscribe?ids=cpu.frequency"), false);
assert.equal(coversSelector("/api/metrics/subscribe?ids=cpu.usage", "/api/metrics/subscribe"), false);
assert.equal(coversSelector("/api/metrics/subscribe", "/api/metrics/subscribe?ids=cpu.usage"), false);
assert.equal(coversSelector("/api/resource-monitor/subscribe?scope=details", "/api/resource-monitor/subscribe?scope=bars"), false);
const payload = { version: 2, processes: [{ processId: 123, name: "原始进程.exe" }], metrics: [] };
let now = 0;
const path = "/api/resource-facts/subscribe?items=process.inventory&items=cpu.usage";
const replay = createSliceReplay({ durationMs: 1000, events: [
  { kind: "frame", path, atMs: 100, body: payload },
  { kind: "frame", path, atMs: 650, body: { ...payload, capturedAt: "original-time" } },
  { kind: "response", path: "/api/software", atMs: 10, body: ["first"] },
  { kind: "response", path: "/api/software", atMs: 800, body: ["second"] }
] }, () => now);
assert.deepEqual(replay.response("/api/software").body, ["first"]);
now = 900;
assert.deepEqual(replay.response("/api/software").body, ["second"]);
assert.equal(replay.response("/api/settings"), null, "uncovered fields retain journey fixture");
const timers = [], cancelled = [], received = [];
now = 0;
const release = replay.subscribe(path, body => received.push(body), (callback, atMs) => { timers.push({ callback, atMs }); return timers.length; }, id => cancelled.push(id));
assert.deepEqual(timers.map(timer => timer.atMs), [100, 650], "original receive offsets, no uniform sampling");
for (const timer of timers) timer.callback();
assert.equal(received[0], payload, "payload is delivered unchanged to production decoder");
release();
assert.deepEqual(cancelled, [1, 2], "close cancels every owned timer");
now = 900;
const late = [];
replay.subscribe(path, body => late.push(body), () => { assert.fail("past frames must not restart their timers"); });
assert.deepEqual(late, [received[1]], "a new channel joins the scenario's shared timeline at the current frame");
console.log("real slice replay contracts passed");

const fieldReplay = createSliceReplay({ durationMs: 1000, events: [{ kind: "frame", atMs: 50,
  path: "/api/metrics/subscribe?fields=cpu_frequency,gpu_activity", body: [
    { deviceIdentity: "cpu-A", fields: [{ name: "cpu_frequency", numericValue: 3200 }] },
    { deviceIdentity: "gpu-B", fields: [{ name: "gpu_activity", numericValue: 30 }] }
  ] }] });
assert.equal(fieldReplay.frames("/api/metrics/subscribe?device=cpu-A&fields=cpu_frequency").length, 1);
assert.equal(fieldReplay.frames("/api/metrics/subscribe?device=gpu-B&fields=cpu_frequency").length, 0);
assert.equal(fieldReplay.frames("/api/metrics/subscribe?fields=unrecorded").length, 0);

// Policy lives on the production read plan; payloads outside that category never
// override the journey, even when the raw slice contains them.
const { deriveProductionReadPlan } = await import('./productionReadPlan.mjs');
const plan = await deriveProductionReadPlan(1000, { metrics: [{ id: 'cpu.usage' }, { id: 'memory.usage' }], metricFields: [] });
assert.ok(plan.categories.observation.subscriptions.some(path => path.startsWith('/api/resource-facts/subscribe')));
assert.ok(plan.categories.observation.reads.includes('/api/resource-breakdown/snapshot'));
assert.ok(plan.categories.observation.subscriptions.some(path => path.startsWith('/api/device-topology/state/subscribe')));
assert.ok(!plan.categories.observation.subscriptions.some(path => /control|operations|updates|management|scheduling/.test(path)));
assert.ok(!plan.categories.observation.reads.some(path => /settings|control|software|updates|disk-usage|recordings|audit/.test(path)));
assert.ok(!plan.categories.observation.reads.includes('/api/gpu/performance-scores'), 'configured GPU scores stay with the override fixture');
const policyReplay = createSliceReplay({ events: [
  { kind: 'response', atMs: 0, path: '/api/local-system/status', body: { uptimeSeconds: 42 } },
  ...['/api/settings/app', '/api/control/catalog', '/api/software', '/api/disk-usage/summary', '/api/updates/product/status', '/api/targeted-recordings'].map(path => ({ kind: 'response', atMs: 0, path, body: 'must stay fixture' })),
  { kind: 'frame', atMs: 0, path: '/api/operations/subscribe', body: 'must stay fixture' }
] }, () => 0, plan.categories.observation);
assert.equal(policyReplay.response('/api/local-system/status').body.uptimeSeconds, 42);
for (const path of ['/api/settings/app', '/api/control/catalog', '/api/software', '/api/disk-usage/summary', '/api/updates/product/status', '/api/targeted-recordings']) assert.equal(policyReplay.response(path), null);
assert.deepEqual(policyReplay.frames('/api/operations/subscribe'), []);

if (process.env.RM_REPLAY_TEST_SLICE) {
  const { startMockServer } = await import('../browser/contracts/mockServerFixture.mjs');
  const server = await startMockServer({ env: { RM_REAL_SLICE: process.env.RM_REPLAY_TEST_SLICE } });
  try {
    const { value: catalog } = await server.json('/api/metrics/catalog');
    const expanded = await deriveProductionReadPlan(1000, { metrics: catalog, metricFields: [] });
    const path = expanded.categories.observation.subscriptions.find(path => path.startsWith('/api/resource-monitor/subscribe'));
    let takeover;
    const frames = await server.frames([{ id: 'bars', path }], frames => {
      if (!takeover) takeover = (async () => {
        await server.scenario({ manualPublishPaths: ['/api/resource-monitor/subscribe'] });
        const { value: published } = await server.json('/__test/subscriptions/publish', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ path: '/api/resource-monitor/subscribe', patch: { resourceBreakdownGeneration: 6 } }) });
        assert.ok(published.published > 0, 'a replay subscriber remains available to the journey frame controller');
      })();
      return frames.length > 1;
    });
    await takeover;
    assert.ok(!JSON.stringify(frames[0]).includes('frontend-harness.software'), 'the live connection starts on the actual recorded payload');
    assert.ok(JSON.stringify(frames.at(-1)).includes('frontend-harness.software'), 'manual publication uses the fixture payload');
    const { value: settings } = await server.json('/api/settings/app');
    assert.equal(settings.revision, 'frontend-harness-r1', 'settings keep the journey revision');
    console.log('real observation replay yields to a live manual bar controller');
  } finally { await server.close(); }
}

// A real catalog is larger than the former 1 MiB probe guard. Observe two
// transport chunks without changing the reader's chunks or retaining payloads.
const { observeSubscriptionFrames } = await import('../browser/subscriptionFrameProbe.js');
const { runInNewContext } = await import('node:vm');
const line = JSON.stringify({ subscriptionId: 'large', value: 'x'.repeat(1_200_000) }) + '\n';
const bytes = new TextEncoder().encode(line);
const response = new Response(new ReadableStream({ start(controller) {
  controller.enqueue(bytes.slice(0, 700_000)); controller.enqueue(bytes.slice(700_000)); controller.close();
} }));
const sandbox = { window: { fetch: async () => response }, location: { href: 'http://localhost/' }, URL, Request, TextDecoder };
await observeSubscriptionFrames({ request: { get: async () => ({ json: async () => ({}) }) },
  addInitScript: async script => runInNewContext(`(${script.toString()})()`, sandbox) }, 'http://localhost');
const observed = await sandbox.window.fetch('/api/subscriptions/stream', { body: JSON.stringify({ subscriptions: [{ id: 'large', path: '/api/metrics/fields/catalog/subscribe' }] }) });
const reader = observed.body.getReader();
assert.equal((await reader.read()).value.length, 700_000);
assert.equal((await reader.read()).value.length, bytes.length - 700_000);
await reader.read();
assert.equal(sandbox.window.__subscriptionFrameProbe.error, null);
assert.equal(sandbox.window.__subscriptionFrameProbe.counts['/api/metrics/fields/catalog/subscribe'], 1);
