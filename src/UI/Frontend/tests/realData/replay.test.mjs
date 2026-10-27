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
