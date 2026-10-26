import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });

test("M03 unknown selectors reject only their ID while valid subscriptions continue", async () => {
  const frames = await server.frames([
    { id: "unknown", path: "/api/not-supported/subscribe" },
    { id: "metrics", path: "/api/metrics/subscribe?ids=cpu.usage&intervalMs=100" }
  ], frames => frames.some(frame => frame.subscriptionId === "unknown") && frames.filter(frame => frame.subscriptionId === "metrics").length >= 2);
  assert.deepEqual(frames.filter(frame => frame.subscriptionId === "unknown"), [{ subscriptionId: "unknown", value: { statusCode: 400, error: "unsupported-subscription-selector" } }]);
  assert(frames.filter(frame => frame.subscriptionId === "metrics").every(frame => frame.value.items["cpu.usage"].status === 1));
});

test("M03 invalid item IDs still reject the whole channel", async () => {
  const { status } = await server.json("/api/subscriptions/stream", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ version: 1, subscriptions: [{ id: "same", path: "/api/metrics/subscribe" }, { id: "same", path: "/api/nope" }] }) });
  assert.equal(status, 400);
});
