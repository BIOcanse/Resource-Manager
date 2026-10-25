import assert from "node:assert/strict";
import { assertSubscriptionChannelReleased, subscriptionChannelConcurrency } from "./browser/subscriptionChannelProbe.js";

const start = (requestId: string, timestamp: number) => ({ requestId, timestamp, delta: 1 });
const end = (requestId: string, timestamp: number) => ({ requestId, timestamp, delta: -1 });
assert.deepEqual(subscriptionChannelConcurrency([
  start("old", 1), start("new", 3), end("old", 2)
]), { inFlight: 1, maxInFlight: 1 }, "late close delivery is not physical overlap");
assert.deepEqual(subscriptionChannelConcurrency([
  start("old", 1), start("new", 2), end("old", 3)
]), { inFlight: 1, maxInFlight: 2 }, "a real overlap must fail the browser assertion");
assert.deepEqual(subscriptionChannelConcurrency([
  start("old", 1), end("old", 2)
]), { inFlight: 0, maxInFlight: 1 }, "a closed channel cannot pass as active");
console.log("Browser subscription probe distinguishes late event delivery from transport overlap.");

assert.deepEqual(assertSubscriptionChannelReleased([start("old", 1), end("old", 2)], 1.5, 10000),
  { releaseMs: 500 }, "late delivery to the runner does not change the browser release time");
assert.deepEqual(assertSubscriptionChannelReleased([start("old", 1), end("old", 11)], 1, 10000),
  { releaseMs: 10000 }, "the ten-second boundary is inclusive");
assert.throws(() => assertSubscriptionChannelReleased([
  start("old", 1), end("old", 2), start("access-requests", 2.001)
], 1.5, 10000), /still active/, "a replacement stream cannot pass through the gap between streams");
assert.throws(() => assertSubscriptionChannelReleased([
  start("old", 1), end("old", 11.001)
], 1, 10000), /release took/, "a release after ten seconds must fail even if delivered before the assertion");
